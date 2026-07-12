using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAI;
using Orkis.Artifacts;
using Orkis.Clients;
using Orkis.Memory;
using Orkis.Retrieval;
using Orkis.Runs;
using Orkis.Sandboxing;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Daemon;

/// <summary>
/// Builds the daemon: Kestrel on a Unix domain socket with the HTTP/JSON + SSE
/// surface, over the same durable stores the CLI host uses. One composition root
/// among several — the libraries stay embeddable in-process.
/// </summary>
public static class DaemonApplication
{
    /// <summary>
    /// Creates the configured daemon. Async because consuming an MCP server means
    /// connecting and listing its tools up front. <paramref name="configureServices"/>
    /// runs after the daemon's own registrations, so tests can replace services.
    /// </summary>
    public static async Task<WebApplication> CreateAsync(
        DaemonSettings settings,
        Action<IServiceCollection>? configureServices = null
    )
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Connect before building: a misconfigured server fails daemon startup
        // loudly instead of leaving a silently tool-less catalogue. Each server is its
        // own McpToolSet; their tools are unioned into the catalogue below.
        var mcpToolSets = new List<McpToolSet>();
        foreach (var serverSpec in settings.McpServers)
        {
            mcpToolSets.Add(await McpToolSet.ConnectAsync(serverSpec).ConfigureAwait(false));
        }

        PrepareSocket(settings.SocketPath);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(settings.SocketPath));
        builder.Services.ConfigureHttpJsonOptions(static options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase))
        );

        var services = builder.Services;
        services.AddOrkis();
        services.AddOrkisFileCheckpointStore(options => options.RootPath = settings.CheckpointDirectory);
        services.AddOrkisFileApprovalInbox(options => options.RootPath = settings.ApprovalDirectory);
        services.AddOrkisFileArtifactStore(options => options.RootPath = settings.ArtifactDirectory);

        // Durable log first, then live fan-out: the broker forwards each event to the
        // file sink before publishing it to SSE subscribers.
        services.AddOrkisFileRunEvents(options => options.RootPath = settings.EventDirectory);
        services.RemoveAll<IRunEventSink>();
        services.AddSingleton<RunEventBroker>(static provider => new RunEventBroker(
            provider.GetRequiredService<FileRunEventSink>()
        ));
        services.AddSingleton<IRunEventSink>(static provider => provider.GetRequiredService<RunEventBroker>());

        switch (settings.Sandbox)
        {
            case "firecracker":
                services.AddOrkisFirecrackerSandbox(options =>
                {
                    options.KernelImagePath =
                        settings.FirecrackerKernelPath
                        ?? throw new InvalidOperationException("Firecracker requires a kernel image path.");
                    options.RootfsImagePath =
                        settings.FirecrackerRootfsPath
                        ?? throw new InvalidOperationException("Firecracker requires a rootfs image path.");
                    options.Network = settings.FirecrackerEgress ? NetworkPolicy.RestrictedEgress : NetworkPolicy.None;
                });
                break;
            case "bubblewrap":
                services.AddOrkisBubblewrapSandbox();
                break;
            default:
                services.AddOrkisProcessSandbox();
                break;
        }

        // Host execution registers alongside the isolation sandbox so an approval can
        // choose it — same lattice the CLI host exposes.
        services.AddOrkisHostSandbox();

        // The daemon has no console to prompt at: queue supervision is the default,
        // and every decision arrives through the approvals endpoints.
        services.AddOrkisSupervisor(SupervisorKeys.Default, CreateQueueSupervisor);
        services.AddOrkisSupervisor("queue", CreateQueueSupervisor);
        services.AddOrkisSupervisor<AutoApproveSupervisor>("yolo");

        // The default (unkeyed) client is the offline script, or the model marked
        // default. Every configured model also registers under its own key, so a run
        // selects one with AgentRunRequest.ModelKey.
        var defaultModel = settings.Offline
            ? null
            : settings.Models.FirstOrDefault(m => m.Key == settings.DefaultModelKey)
                ?? (settings.Models.Count > 0 ? settings.Models[0] : null);
        services.AddSingleton(
            new ChatClientBuilder(defaultModel is null ? new OfflineChatClient() : BuildChatClient(defaultModel))
                .Use(static inner => new ResilientChatClient(inner))
                .ConfigureOptions(options => options.ModelId ??= defaultModel?.ModelId)
                .Build()
        );

        foreach (var model in settings.Models)
        {
            services.AddOrkisChatClient(
                model.Key,
                _ =>
                    new ChatClientBuilder(BuildChatClient(model))
                        .Use(static inner => new ResilientChatClient(inner))
                        .ConfigureOptions(options => options.ModelId ??= model.ModelId)
                        .Build()
            );
        }

        if (!settings.Offline)
        {
            // AI first-line review needs a live model; escalations land in the inbox.
            services.AddOrkisSupervisor(
                "ai",
                static provider => new ThresholdSupervisor(
                    ToolRisk.ReadOnly,
                    new ChatClientSupervisor(
                        provider.GetRequiredService<IChatClient>(),
                        new QueueSupervisor(
                            provider.GetRequiredService<IApprovalInbox>(),
                            provider.GetRequiredService<TimeProvider>()
                        )
                    )
                )
            );
        }

        // Memory and retrieval need an embeddings endpoint; with one configured, the
        // SQLite memory store plus save/search tools come on, and a corpus directory
        // additionally enables search_corpus (reranked with the chat model).
        if (!settings.Offline && settings.Embedding is { } embedding)
        {
            services.AddSingleton(
                BuildOpenAIClient(embedding).GetEmbeddingClient(embedding.ModelId).AsIEmbeddingGenerator()
            );
            services.AddOrkisSqliteMemoryStore(options =>
                options.DatabasePath =
                    settings.MemoryDatabasePath
                    ?? throw new InvalidOperationException("Embeddings are on but no memory database path is set.")
            );

            if (settings.CorpusDirectory is { Length: > 0 })
            {
                services.AddOrkisInMemoryRag();
                services.AddOrkisHtmlParser();
                services.AddOrkisPdfParser();
                services.AddOrkisSqliteVectorStore(options =>
                    options.DatabasePath =
                        settings.CorpusDatabasePath
                        ?? throw new InvalidOperationException(
                            "A corpus directory is set but no corpus database path is."
                        )
                );
                services.AddOrkisChatClientReranker();
            }
        }

        // Storage-bearing tools (shell, artifacts, memory) are not registered as
        // singletons: RunnerFactory builds them per run, so chats get their own
        // workspace and memory scope.
        services.AddSingleton(provider => new RunnerFactory(provider, settings));

        if (mcpToolSets.Count > 0)
        {
            // Each instance registered as a captured singleton so provider disposal
            // shuts its server down; the catalogue is the union of all their tools.
            foreach (var toolSet in mcpToolSets)
            {
                services.AddSingleton(toolSet);
            }

            services.AddOrkisToolCatalog(_ => mcpToolSets.SelectMany(toolSet => toolSet.Tools));
        }

        services.AddSingleton(
            new DaemonInfo
            {
                Sandbox = settings.Sandbox,
                DefaultModel = defaultModel?.ModelId,
                MemoryEnabled = !settings.Offline && settings.Embedding is not null,
                CorpusEnabled =
                    !settings.Offline && settings.Embedding is not null && settings.CorpusDirectory is { Length: > 0 },
            }
        );
        services.AddSingleton<RunExecutor>();

        // Schedules: fired by a background service, persisted so they survive restart.
        // Defaults beside the checkpoints when unset, so embedding callers need not
        // configure a path.
        var scheduleDirectory = string.IsNullOrEmpty(settings.ScheduleDirectory)
            ? Path.Combine(settings.CheckpointDirectory, "..", "schedules")
            : settings.ScheduleDirectory;
        services.AddOrkisFileScheduleStore(options => options.RootPath = scheduleDirectory);
        services.AddHostedService<ScheduleRunner>();
        services.AddHostedService<ScheduleHandoffService>();

        configureServices?.Invoke(services);

        var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() => TightenSocketPermissions(settings.SocketPath));
        app.MapOrkisDaemon();
        return app;
    }

    private static IChatClient BuildChatClient(ResolvedModel model) =>
        model.Kind switch
        {
            ProviderKind.Anthropic => new AnthropicClient(model.ApiKey).Messages,
            ProviderKind.OpenAI => BuildOpenAIClient(model).GetChatClient(model.ModelId).AsIChatClient(),
            _ => throw new InvalidOperationException($"Unknown provider kind for model '{model.Key}'."),
        };

    /// <summary>
    /// An OpenAI client honoring a custom base URL, so OpenAI-compatible providers
    /// (OpenRouter, a local server, …) work through the same path as OpenAI itself.
    /// </summary>
    private static OpenAIClient BuildOpenAIClient(ResolvedModel model) =>
        model.BaseUrl is { Length: > 0 } baseUrl
            ? new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(model.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
            )
            : new OpenAIClient(model.ApiKey);

    private static ISupervisor CreateQueueSupervisor(IServiceProvider provider) =>
        new ThresholdSupervisor(
            ToolRisk.ReadOnly,
            new QueueSupervisor(
                provider.GetRequiredService<IApprovalInbox>(),
                provider.GetRequiredService<TimeProvider>()
            )
        );

    /// <summary>
    /// Ensures the socket can be bound: creates its directory owner-only, refuses to
    /// displace a live daemon, and removes a stale socket left by a crash.
    /// </summary>
    private static void PrepareSocket(string socketPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(socketPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }
        }

        if (!File.Exists(socketPath))
        {
            return;
        }

        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(socketPath));
            throw new InvalidOperationException($"Another daemon is already listening on '{socketPath}'.");
        }
        catch (SocketException)
        {
            // Nothing accepted the connection: a stale socket from a dead process.
            File.Delete(socketPath);
        }
    }

    private static void TightenSocketPermissions(string socketPath)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(socketPath))
        {
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
