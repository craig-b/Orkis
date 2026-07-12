using Orkis.Daemon;
using Orkis.Sandboxing;

// The daemon is configured the same way as the CLI host — environment variables for
// paths and providers — so the two composition roots stay interchangeable.
var offline = args.Contains("--offline") || Environment.GetEnvironmentVariable("ORKIS_OFFLINE") == "1";

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
    Console.Error.WriteLine("No API key found. Set ANTHROPIC_API_KEY or OPENAI_API_KEY for live runs");
    Console.Error.WriteLine("(ORKIS_PROVIDER=anthropic|openai picks explicitly), or pass --offline.");
    return 1;
}

var model =
    Environment.GetEnvironmentVariable("ORKIS_MODEL") ?? (provider == "openai" ? "gpt-5-mini" : "claude-sonnet-5");

var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis");

// Sandbox: strongest available wins, overridable with ORKIS_SANDBOX=firecracker|bubblewrap|process.
var firecrackerHome =
    Environment.GetEnvironmentVariable("ORKIS_FIRECRACKER_HOME") ?? Path.Combine(dataRoot, "firecracker");
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

// ORKIS_MODELS registers additional models selectable per run (orkis run --model <key>):
//   ORKIS_MODELS="mini=openai:gpt-5-mini,sonnet=anthropic:claude-sonnet-5"
var models = new List<ModelRegistration>();
if (!offline && Environment.GetEnvironmentVariable("ORKIS_MODELS") is { Length: > 0 } modelSpecs)
{
    foreach (var spec in modelSpecs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var keyAndTarget = spec.Split('=', 2);
        var providerAndModel = keyAndTarget.Length == 2 ? keyAndTarget[1].Split(':', 2) : [];
        if (keyAndTarget.Length != 2 || providerAndModel.Length != 2)
        {
            Console.Error.WriteLine($"ORKIS_MODELS entry '{spec}' is not key=provider:model.");
            return 1;
        }

        var providerApiKey = providerAndModel[0] switch
        {
            "anthropic" => anthropicKey,
            "openai" => openAiKey,
            _ => null,
        };
        if (string.IsNullOrEmpty(providerApiKey))
        {
            Console.Error.WriteLine($"ORKIS_MODELS entry '{spec}': no API key for provider '{providerAndModel[0]}'.");
            return 1;
        }

        models.Add(new ModelRegistration(keyAndTarget[0], providerAndModel[0], providerAndModel[1], providerApiKey));
    }
}

var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
var socketPath =
    Environment.GetEnvironmentVariable("ORKIS_SOCKET")
    ?? Path.Combine(string.IsNullOrEmpty(runtimeDir) ? dataRoot : Path.Combine(runtimeDir, "orkis"), "orkis.sock");

var settings = new DaemonSettings
{
    SocketPath = socketPath,
    CheckpointDirectory =
        Environment.GetEnvironmentVariable("ORKIS_CHECKPOINT_DIR") ?? Path.Combine(dataRoot, "checkpoints"),
    EventDirectory = Environment.GetEnvironmentVariable("ORKIS_EVENT_DIR") ?? Path.Combine(dataRoot, "events"),
    ApprovalDirectory = Environment.GetEnvironmentVariable("ORKIS_APPROVAL_DIR") ?? Path.Combine(dataRoot, "approvals"),
    ArtifactDirectory = Environment.GetEnvironmentVariable("ORKIS_ARTIFACT_DIR") ?? Path.Combine(dataRoot, "artifacts"),
    ScheduleDirectory = Environment.GetEnvironmentVariable("ORKIS_SCHEDULE_DIR") ?? Path.Combine(dataRoot, "schedules"),
    WorkspaceKey = Environment.GetEnvironmentVariable("ORKIS_WORKSPACE") ?? "default",
    Offline = offline,
    Provider = provider,
    ApiKey = selectedKey,
    Model = offline ? null : model,
    Models = models,
    // Memory and retrieval need an embeddings endpoint (OpenAI has one; Anthropic
    // does not) — they stay off without it.
    EmbeddingModel =
        !offline && provider == "openai"
            ? Environment.GetEnvironmentVariable("ORKIS_EMBEDDING_MODEL") ?? "text-embedding-3-small"
            : null,
    MemoryDatabasePath = Environment.GetEnvironmentVariable("ORKIS_MEMORY_DB") ?? Path.Combine(dataRoot, "memory.db"),
    CorpusDirectory = Environment.GetEnvironmentVariable("ORKIS_CORPUS_DIR"),
    CorpusDatabasePath = Path.Combine(dataRoot, "corpus.db"),
    Sandbox = sandbox,
    FirecrackerKernelPath = firecrackerKernel,
    FirecrackerRootfsPath = firecrackerRootfs,
    FirecrackerEgress = Environment.GetEnvironmentVariable("ORKIS_NETWORK")?.ToLowerInvariant() == "egress",
    McpServer = Environment.GetEnvironmentVariable("ORKIS_MCP_SERVER"),
};

var app = await DaemonApplication.CreateAsync(settings);

if (settings.EmbeddingModel is not null && settings.CorpusDirectory is { Length: > 0 } corpusDirectory)
{
    var (documents, chunks) = await app
        .Services.GetRequiredService<Orkis.Retrieval.DirectoryCorpusLoader>()
        .LoadAsync(corpusDirectory);
    Console.WriteLine($"corpus: indexed {documents} document(s) as {chunks} chunk(s) from {corpusDirectory}");
}

Console.WriteLine($"orkis daemon | listening on unix:{settings.SocketPath}");
Console.WriteLine($"mode: {(offline ? "offline (scripted model)" : $"live ({provider}: {model})")}");
if (models.Count > 0)
{
    Console.WriteLine($"models: {string.Join(", ", models.Select(m => $"{m.Key} ({m.Provider}: {m.ModelId})"))}");
}
Console.WriteLine($"sandbox: {sandbox} isolation + host execution (granted per approval)");
Console.WriteLine($"supervision: queue (default) | yolo{(offline ? "" : " | ai")}");
Console.WriteLine(
    settings.EmbeddingModel is null
        ? "memory: off (no embeddings endpoint for this provider)"
        : $"memory: on ({settings.EmbeddingModel}){(settings.CorpusDirectory is null ? "" : " + corpus retrieval")}"
);
if (app.Services.GetService<Orkis.Tools.McpToolSet>() is { } mcpToolSet)
{
    Console.WriteLine(
        $"mcp: {mcpToolSet.Tools.Count} tool(s) join the catalogue: "
            + string.Join(", ", mcpToolSet.Tools.Select(t => t.Descriptor.Name))
    );
}

Console.WriteLine($"data: {dataRoot}");

await app.RunAsync();
return 0;
