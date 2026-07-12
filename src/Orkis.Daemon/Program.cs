using Orkis.Daemon;
using Orkis.Sandboxing;

var offline = args.Contains("--offline") || Environment.GetEnvironmentVariable("ORKIS_OFFLINE") == "1";
var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis");

// Models come from the config file (JSONC providers + models) when present, otherwise
// the legacy environment variables. Offline uses the scripted model and needs neither.
IReadOnlyList<ResolvedModel> models = [];
string? defaultModelKey = null;
ResolvedModel? embedding = null;
if (!offline)
{
    try
    {
        if (OrkisConfig.Load() is { } config)
        {
            (models, defaultModelKey, embedding) = (config.Models, config.DefaultModelKey, config.Embedding);
        }
        else if (LegacyModelsFromEnvironment() is { } legacy)
        {
            (models, defaultModelKey, embedding) = legacy;
        }
    }
    catch (OrkisConfigException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    if (models.Count == 0)
    {
        Console.Error.WriteLine($"No models configured. Create a config file (providers + models) at:");
        Console.Error.WriteLine($"  {OrkisConfig.DefaultPath()}");
        Console.Error.WriteLine("or set ANTHROPIC_API_KEY / OPENAI_API_KEY for a default, or pass --offline.");
        return 1;
    }
}

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
    Models = models,
    DefaultModelKey = defaultModelKey,
    Embedding = embedding,
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

if (settings.Embedding is not null && settings.CorpusDirectory is { Length: > 0 } corpusDirectory)
{
    var (documents, chunks) = await app
        .Services.GetRequiredService<Orkis.Retrieval.DirectoryCorpusLoader>()
        .LoadAsync(corpusDirectory);
    Console.WriteLine($"corpus: indexed {documents} document(s) as {chunks} chunk(s) from {corpusDirectory}");
}

Console.WriteLine($"orkis daemon | listening on unix:{settings.SocketPath}");
if (offline)
{
    Console.WriteLine("mode: offline (scripted model)");
}
else
{
    Console.WriteLine(
        "models: "
            + string.Join(
                ", ",
                models.Select(m =>
                    $"{m.Key}{(m.Key == defaultModelKey ? "*" : "")} ({m.Kind.ToString().ToLowerInvariant()}: {m.ModelId})"
                )
            )
            + " (* = default)"
    );
}
Console.WriteLine($"sandbox: {sandbox} isolation + host execution (granted per approval)");
Console.WriteLine($"supervision: queue (default) | yolo{(offline ? "" : " | ai")}");
Console.WriteLine(
    settings.Embedding is null
        ? "memory: off (no embedding model configured)"
        : $"memory: on ({settings.Embedding.ModelId}){(settings.CorpusDirectory is null ? "" : " + corpus retrieval")}"
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

// Legacy provider config: a default model from ANTHROPIC_API_KEY / OPENAI_API_KEY (or
// ORKIS_PROVIDER + ORKIS_MODEL), additional models from ORKIS_MODELS, and an OpenAI
// embedding model — used only when no config file exists. Returns null when no key is
// set. Throws OrkisConfigException on a malformed ORKIS_MODELS entry.
static (IReadOnlyList<ResolvedModel>, string?, ResolvedModel?)? LegacyModelsFromEnvironment()
{
    var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    var provider =
        Environment.GetEnvironmentVariable("ORKIS_PROVIDER")?.ToLowerInvariant()
        ?? (
            !string.IsNullOrEmpty(anthropicKey) ? "anthropic"
            : !string.IsNullOrEmpty(openAiKey) ? "openai"
            : null
        );
    var key = provider switch
    {
        "anthropic" => anthropicKey,
        "openai" => openAiKey,
        _ => null,
    };
    if (string.IsNullOrEmpty(key))
    {
        return null;
    }

    var kind = provider == "openai" ? ProviderKind.OpenAI : ProviderKind.Anthropic;
    var modelId =
        Environment.GetEnvironmentVariable("ORKIS_MODEL") ?? (provider == "openai" ? "gpt-5-mini" : "claude-sonnet-5");
    var list = new List<ResolvedModel> { new("default", kind, null, key, modelId) };

    if (Environment.GetEnvironmentVariable("ORKIS_MODELS") is { Length: > 0 } specs)
    {
        foreach (var spec in specs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = spec.Split('=', 2);
            var pm = kv.Length == 2 ? kv[1].Split(':', 2) : [];
            if (kv.Length != 2 || pm.Length != 2)
            {
                throw new OrkisConfigException($"ORKIS_MODELS entry '{spec}' is not key=provider:model.");
            }

            var modelKey = pm[0] switch
            {
                "anthropic" => anthropicKey,
                "openai" => openAiKey,
                _ => null,
            };
            if (string.IsNullOrEmpty(modelKey))
            {
                throw new OrkisConfigException($"ORKIS_MODELS entry '{spec}': no API key for provider '{pm[0]}'.");
            }

            list.Add(
                new(kv[0], pm[0] == "openai" ? ProviderKind.OpenAI : ProviderKind.Anthropic, null, modelKey, pm[1])
            );
        }
    }

    var embedding = openAiKey is { Length: > 0 }
        ? new ResolvedModel(
            "embedding",
            ProviderKind.OpenAI,
            null,
            openAiKey,
            Environment.GetEnvironmentVariable("ORKIS_EMBEDDING_MODEL") ?? "text-embedding-3-small"
        )
        : null;

    return (list, "default", embedding);
}
