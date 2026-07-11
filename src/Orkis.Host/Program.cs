using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Orkis.Agents;
using Orkis.Host;
using Orkis.Runs;
using Orkis.Sandboxing;
using Orkis.Supervision;
using Orkis.Tools;

var offline = args.Contains("--offline");
var yolo = args.Contains("--yolo");
var prompt =
    args.FirstOrDefault(static a => !a.StartsWith('-'))
    ?? "Check the current time, then run a shell command that prints a greeting and the working directory.";

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
services.AddOrkisSupervisor<ConsoleSupervisor>();
services.AddOrkisSupervisor<AutoApproveSupervisor>("yolo");
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

services.AddSingleton<ITool>(sp => new ConsoleLoggingTool(new ShellTool(sp.GetRequiredService<ISandbox>())));

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

var request = new AgentRunRequest
{
    Prompt = prompt,
    SystemPrompt =
        "You are the Orkis demo agent. Use the available tools to fulfil the request, then summarize what happened.",
    SupervisorKey = yolo ? "yolo" : SupervisorKeys.Default,
    Budget = new RunBudget { MaxToolCalls = 10, MaxTokens = 100_000 },
};

Console.WriteLine($"orkis demo | run {request.RunId}");
Console.WriteLine($"mode: {(offline ? "offline (scripted model)" : $"live ({provider}: {model})")}");
Console.WriteLine($"supervision: {request.SupervisorKey}");
Console.WriteLine($"sandbox: {sandbox}");
Console.WriteLine($"prompt: {prompt}");
Console.WriteLine();

var result = await runner.StartAsync(request);

Console.WriteLine();
Console.WriteLine($"status: {result.Status}");
Console.WriteLine($"response: {result.FinalText}");
Console.WriteLine(
    $"usage: {result.Usage.InputTokens} in / {result.Usage.OutputTokens} out tokens, "
        + $"{result.Usage.ToolCalls} tool call(s), cost {result.Usage.Cost:0.####}, "
        + $"active {result.Usage.ActiveDuration.TotalSeconds:0.00}s"
);

return result.Status == RunStatus.Completed ? 0 : 2;
