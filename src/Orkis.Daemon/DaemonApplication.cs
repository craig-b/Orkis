using System.Net.Sockets;
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
        // loudly instead of leaving a silently tool-less catalogue.
        var mcpToolSet = settings.McpServer is { Length: > 0 } serverSpec
            ? await McpToolSet.ConnectAsync(serverSpec).ConfigureAwait(false)
            : null;

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

        IChatClient providerClient = settings.Offline
            ? new OfflineChatClient()
            : settings.Provider switch
            {
                "openai" => new OpenAIClient(settings.ApiKey).GetChatClient(settings.Model!).AsIChatClient(),
                _ => new AnthropicClient(settings.ApiKey).Messages,
            };
        services.AddSingleton(
            new ChatClientBuilder(providerClient)
                .Use(static inner => new ResilientChatClient(inner))
                .ConfigureOptions(options => options.ModelId ??= settings.Model)
                .Build()
        );

        // Additional models register under per-run keys — the keyed-supervisor
        // pattern applied to chat clients. The unkeyed client above stays the default.
        foreach (var model in settings.Models)
        {
            services.AddOrkisChatClient(
                model.Key,
                _ =>
                {
                    IChatClient keyedClient = model.Provider switch
                    {
                        "openai" => new OpenAIClient(model.ApiKey).GetChatClient(model.ModelId).AsIChatClient(),
                        "anthropic" => new AnthropicClient(model.ApiKey).Messages,
                        _ => throw new InvalidOperationException(
                            $"Unknown provider '{model.Provider}' for model key '{model.Key}'."
                        ),
                    };
                    return new ChatClientBuilder(keyedClient)
                        .Use(static inner => new ResilientChatClient(inner))
                        .ConfigureOptions(options => options.ModelId ??= model.ModelId)
                        .Build();
                }
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
        if (!settings.Offline && settings.EmbeddingModel is { Length: > 0 } embeddingModel)
        {
            services.AddSingleton(
                new OpenAIClient(settings.ApiKey).GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator()
            );
            services.AddOrkisSqliteMemoryStore(options =>
                options.DatabasePath =
                    settings.MemoryDatabasePath
                    ?? throw new InvalidOperationException("Embeddings are on but no memory database path is set.")
            );
            services.AddSingleton<ITool>(provider => new SaveMemoryTool(
                provider.GetRequiredService<IMemoryStore>(),
                timeProvider: provider.GetRequiredService<TimeProvider>()
            ));
            services.AddSingleton<ITool>(provider => new SearchMemoriesTool(
                provider.GetRequiredService<IMemoryStore>()
            ));

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
                services.AddSingleton<ITool>(provider => new RetrievalTool(
                    provider.GetRequiredService<IRetriever>(),
                    provider.GetService<IReranker>()
                ));
            }
        }

        services.AddSingleton<ITool>(provider => new ShellTool(
            provider.GetServices<ISandbox>(),
            settings.WorkspaceKey
        ));
        services.AddSingleton<ITool>(provider => new PromoteArtifactTool(
            provider.GetServices<ISandbox>(),
            provider.GetRequiredService<IArtifactStore>(),
            settings.WorkspaceKey
        ));
        services.AddSingleton<ITool>(provider => new StageArtifactTool(
            provider.GetServices<ISandbox>(),
            provider.GetRequiredService<IArtifactStore>(),
            settings.WorkspaceKey
        ));

        if (mcpToolSet is not null)
        {
            services.AddSingleton(mcpToolSet); // Provider disposal shuts the server down.
            services.AddOrkisToolCatalog(_ => mcpToolSet.Tools);
        }

        services.AddSingleton(
            new DaemonInfo
            {
                Sandbox = settings.Sandbox,
                DefaultModel = settings.Model,
                MemoryEnabled = !settings.Offline && settings.EmbeddingModel is { Length: > 0 },
                CorpusEnabled =
                    !settings.Offline
                    && settings.EmbeddingModel is { Length: > 0 }
                    && settings.CorpusDirectory is { Length: > 0 },
            }
        );
        services.AddSingleton<RunExecutor>();

        configureServices?.Invoke(services);

        var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() => TightenSocketPermissions(settings.SocketPath));
        app.MapOrkisDaemon();
        return app;
    }

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
