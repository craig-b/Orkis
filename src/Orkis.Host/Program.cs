using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Orkis.Agents;
using Orkis.Artifacts;
using Orkis.Host;
using Orkis.Runs;
using Orkis.Sandboxing;
using Orkis.Supervision;
using Orkis.Tools;

var argList = args.ToList();
string? resumeRunId = null;
var resumeIndex = argList.IndexOf("--resume");
if (resumeIndex >= 0)
{
    if (resumeIndex + 1 >= argList.Count)
    {
        Console.Error.WriteLine("--resume requires a run id (shown when the run starts).");
        return 1;
    }

    resumeRunId = argList[resumeIndex + 1];
    argList.RemoveRange(resumeIndex, 2);
}

var offline = argList.Contains("--offline");
var yolo = argList.Contains("--yolo");
var queueMode = argList.Contains("--queue");
var prompt =
    argList.FirstOrDefault(static a => !a.StartsWith('-'))
    ?? "Check the current time, then run a shell command that prints a greeting and the working directory.";

// The approval inbox verbs operate on the queue directly — no model or agent needed.
var approvalsDir =
    Environment.GetEnvironmentVariable("ORKIS_APPROVAL_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis", "approvals");
IApprovalInbox CreateApprovalInbox() =>
    new FileApprovalInbox(
        Microsoft.Extensions.Options.Options.Create(new FileApprovalInboxOptions { RootPath = approvalsDir })
    );

var artifactsDir =
    Environment.GetEnvironmentVariable("ORKIS_ARTIFACT_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis", "artifacts");

if (argList.Contains("--artifacts"))
{
    var store = new FileArtifactStore(
        Microsoft.Extensions.Options.Options.Create(new FileArtifactStoreOptions { RootPath = artifactsDir })
    );
    var artifacts = await store.ListAsync();
    if (artifacts.Count == 0)
    {
        Console.WriteLine("no artifacts");
        return 0;
    }

    foreach (var artifact in artifacts)
    {
        Console.WriteLine($"{artifact.CreatedAt:u}  {artifact.Length,10}  {artifact.Name}");
    }

    return 0;
}

if (argList.Contains("--approvals"))
{
    var pending = await CreateApprovalInbox().ListPendingAsync();
    if (pending.Count == 0)
    {
        Console.WriteLine("no pending approvals");
        return 0;
    }

    foreach (var approval in pending)
    {
        Console.WriteLine($"run {approval.RunId}");
        Console.WriteLine(
            $"  call {approval.Call.Id} — {approval.Call.ToolName} "
                + $"(risk: {approval.Tool.Risk}, requested {approval.RequestedAt:u})"
        );
        Console.WriteLine($"  arguments: {approval.Call.Arguments.GetRawText()}");
        Console.WriteLine($"  decide: --approve {approval.Call.Id} [h|s] | --deny {approval.Call.Id} [reason]");
    }

    return 0;
}

var approveIndex = argList.IndexOf("--approve");
var denyIndex = argList.IndexOf("--deny");
if (approveIndex >= 0 || denyIndex >= 0)
{
    var approving = approveIndex >= 0;
    var verbIndex = approving ? approveIndex : denyIndex;
    if (verbIndex + 1 >= argList.Count || argList[verbIndex + 1].StartsWith('-'))
    {
        Console.Error.WriteLine($"{(approving ? "--approve" : "--deny")} requires a call id (see --approvals).");
        return 1;
    }

    var callId = argList[verbIndex + 1];
    var extra = verbIndex + 2 < argList.Count ? argList[verbIndex + 2] : null;
    if (extra is not null && extra.StartsWith('-'))
    {
        extra = null;
    }

    var queue = CreateApprovalInbox();
    var matches = (await queue.ListPendingAsync()).Where(p => p.Call.Id == callId).ToList();
    if (matches.Count == 0)
    {
        Console.Error.WriteLine($"No pending approval with call id '{callId}'.");
        return 1;
    }

    if (matches.Count > 1)
    {
        Console.Error.WriteLine($"Call id '{callId}' is pending in multiple runs; cannot decide unambiguously.");
        return 1;
    }

    var target = matches[0];
    var decision = approving
        ? extra is "s" or "sandbox" ? SupervisionDecision.Approve(SandboxLevel.Standard) : SupervisionDecision.Approve()
        : SupervisionDecision.Deny(extra ?? "The operator denied this action.");
    await queue.DecideAsync(target.RunId, target.Call.Id, decision);

    Console.WriteLine($"{(approving ? "approved" : "denied")}: {target.Call.ToolName} (call {callId})");
    Console.WriteLine($"resume the run with: --resume {target.RunId}");
    return 0;
}

var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var provider = Environment.GetEnvironmentVariable("ORKIS_PROVIDER")?.ToLowerInvariant();
provider ??=
    !string.IsNullOrEmpty(anthropicKey) ? "anthropic"
    : !string.IsNullOrEmpty(openAiKey) ? "openai"
    : null;

var selectedKey = provider switch
{
    "anthropic" => anthropicKey,
    "openai" => openAiKey,
    _ => null,
};
if (!offline && string.IsNullOrEmpty(selectedKey))
{
    Console.Error.WriteLine("No API key found. Set ANTHROPIC_API_KEY or OPENAI_API_KEY for a live run");
    Console.Error.WriteLine("(ORKIS_PROVIDER=anthropic|openai picks explicitly), or pass --offline.");
    return 1;
}

var model =
    Environment.GetEnvironmentVariable("ORKIS_MODEL") ?? (provider == "openai" ? "gpt-5-mini" : "claude-sonnet-5");

// Sandbox: strongest available wins, overridable with ORKIS_SANDBOX=firecracker|bubblewrap|process.
var firecrackerHome =
    Environment.GetEnvironmentVariable("ORKIS_FIRECRACKER_HOME")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis", "firecracker");
var firecrackerKernel = Path.Combine(firecrackerHome, "vmlinux");
var firecrackerRootfs = Path.Combine(firecrackerHome, "rootfs.ext4");
var firecrackerReady =
    FirecrackerSandbox.IsSupported() && File.Exists(firecrackerKernel) && File.Exists(firecrackerRootfs);

var sandbox =
    Environment.GetEnvironmentVariable("ORKIS_SANDBOX")?.ToLowerInvariant()
    ?? (
        firecrackerReady ? "firecracker"
        : await BubblewrapSandbox.IsSupportedAsync() ? "bubblewrap"
        : "process"
    );

if (sandbox == "firecracker" && !firecrackerReady)
{
    Console.Error.WriteLine("Firecracker is not ready (KVM, binary, or guest images missing).");
    Console.Error.WriteLine("Run scripts/setup-firecracker.sh, or choose another ORKIS_SANDBOX.");
    return 1;
}

var services = new ServiceCollection();
services.AddOrkis();

// Durable checkpoints: runs survive a process restart and can be picked up with --resume.
var checkpointDir =
    Environment.GetEnvironmentVariable("ORKIS_CHECKPOINT_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis", "checkpoints");
services.AddOrkisFileCheckpointStore(options => options.RootPath = checkpointDir);
switch (sandbox)
{
    case "firecracker":
        services.AddOrkisFirecrackerSandbox(options =>
        {
            options.KernelImagePath = firecrackerKernel;
            options.RootfsImagePath = firecrackerRootfs;
        });
        break;
    case "bubblewrap":
        services.AddOrkisBubblewrapSandbox();
        break;
    default:
        services.AddOrkisProcessSandbox();
        break;
}

// Register host execution alongside the isolation sandbox (added after it, so the
// isolation sandbox's TryAdd still wins as the primary ISandbox). ShellTool resolves
// the whole collection and picks per supervision decision: [h]ost runs here.
services.AddOrkisHostSandbox();
services.AddOrkisSupervisor<ConsoleSupervisor>();
services.AddOrkisSupervisor<AutoApproveSupervisor>("yolo");

// Queue supervision (--queue): read-only tools pass, everything else parks in the
// durable approval inbox and the run pauses until a decision arrives out of band.
services.AddOrkisFileApprovalInbox(options => options.RootPath = approvalsDir);
services.AddOrkisSupervisor(
    "queue",
    static sp => new ThresholdSupervisor(
        ToolRisk.ReadOnly,
        new QueueSupervisor(sp.GetRequiredService<IApprovalInbox>(), sp.GetRequiredService<TimeProvider>())
    )
);
services.AddOrkisPricing(cost =>
{
    // Indicative prices per million tokens — verify against current published pricing.
    ModelPrice price;
    if (provider == "openai")
    {
        price = new ModelPrice { InputPerMillionTokens = 0.25m, OutputPerMillionTokens = 2m };
    }
    else
    {
        price = new ModelPrice { InputPerMillionTokens = 3m, OutputPerMillionTokens = 15m };
        price.AdditionalPerMillionTokens["cache_read_input_tokens"] = 0.3m;
        price.AdditionalPerMillionTokens["cache_creation_input_tokens"] = 3.75m;
    }

    cost.Models[model] = price;
});

foreach (var tool in DemoTools.CreateOrkisTools())
{
    services.AddSingleton<ITool>(new ConsoleLoggingTool(tool));
}

// Shell commands share one persistent workspace per sandbox type: files written by
// one command are there for the next, across runs and restarts. Files never move
// between sandbox types — that is what artifact promotion (roadmap) is for.
var workspace = Environment.GetEnvironmentVariable("ORKIS_WORKSPACE") ?? "default";
services.AddSingleton<ITool>(sp => new ConsoleLoggingTool(new ShellTool(sp.GetServices<ISandbox>(), workspace)));

// Artifacts: the only path files take across isolation levels. Promotion and staging
// are ordinary tools, so every crossing is a supervisable, auditable decision.
services.AddOrkisFileArtifactStore(options => options.RootPath = artifactsDir);
services.AddSingleton<ITool>(sp => new ConsoleLoggingTool(
    new PromoteArtifactTool(sp.GetServices<ISandbox>(), sp.GetRequiredService<IArtifactStore>(), workspace)
));
services.AddSingleton<ITool>(sp => new ConsoleLoggingTool(
    new StageArtifactTool(sp.GetServices<ISandbox>(), sp.GetRequiredService<IArtifactStore>(), workspace)
));

IChatClient providerClient = offline
    ? new OfflineChatClient()
    : provider switch
    {
        "openai" => new OpenAIClient(openAiKey).GetChatClient(model).AsIChatClient(),
        _ => new AnthropicClient(anthropicKey).Messages,
    };

services.AddSingleton(
    new ChatClientBuilder(providerClient).ConfigureOptions(options => options.ModelId ??= model).Build()
);

await using var serviceProvider = services.BuildServiceProvider();
var runner = serviceProvider.GetRequiredService<AgentRunner>();

AgentRunResult result;
if (resumeRunId is not null)
{
    // Budget, transcript, and supervisor key all come from the checkpoint.
    Console.WriteLine($"orkis demo | resuming run {resumeRunId}");
    Console.WriteLine($"mode: {(offline ? "offline (scripted model)" : $"live ({provider}: {model})")}");
    Console.WriteLine($"sandbox: {sandbox} isolation + host execution ([h]/[s] at each prompt)");
    Console.WriteLine($"checkpoints: {checkpointDir}");
    Console.WriteLine();

    try
    {
        result = await runner.ResumeAsync(resumeRunId);
    }
    catch (InvalidOperationException ex)
    {
        // No checkpoint under this id, or the run already ended.
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}
else
{
    var request = new AgentRunRequest
    {
        Prompt = prompt,
        SystemPrompt =
            "You are the Orkis demo agent. Use the available tools to fulfil the request, then summarize what happened."
            + "\n\n"
            + SystemPromptFragments.ConfabulationGuardrail,
        SupervisorKey = yolo ? "yolo"
            : queueMode ? "queue"
            : SupervisorKeys.Default,
        Budget = new RunBudget { MaxToolCalls = 10, MaxTokens = 100_000 },
    };

    Console.WriteLine($"orkis demo | run {request.RunId}");
    Console.WriteLine($"mode: {(offline ? "offline (scripted model)" : $"live ({provider}: {model})")}");
    Console.WriteLine($"supervision: {request.SupervisorKey}");
    Console.WriteLine($"sandbox: {sandbox} isolation + host execution ([h]/[s] at each prompt)");
    Console.WriteLine($"workspace: {workspace} (persistent per sandbox type; ORKIS_WORKSPACE overrides)");
    Console.WriteLine($"checkpoints: {checkpointDir} (interrupted? resume with --resume {request.RunId})");
    Console.WriteLine($"prompt: {prompt}");
    Console.WriteLine();

    result = await runner.StartAsync(request);
}

Console.WriteLine();
Console.WriteLine($"status: {result.Status}");
Console.WriteLine($"response: {result.FinalText}");
Console.WriteLine(
    $"usage: {result.Usage.InputTokens} in / {result.Usage.OutputTokens} out tokens, "
        + $"{result.Usage.ToolCalls} tool call(s), cost {result.Usage.Cost:0.####}, "
        + $"active {result.Usage.ActiveDuration.TotalSeconds:0.00}s"
);

if (result.Status == RunStatus.AwaitingSupervision)
{
    Console.WriteLine($"awaiting approval — list with --approvals, decide, then rerun with --resume {result.RunId}");
}

return result.Status == RunStatus.Completed ? 0 : 2;
