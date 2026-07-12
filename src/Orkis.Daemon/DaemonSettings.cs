namespace Orkis.Daemon;

/// <summary>
/// Resolved configuration for one daemon instance. <c>Program</c> builds this from
/// environment variables and arguments; tests construct it directly.
/// </summary>
public sealed record DaemonSettings
{
    /// <summary>
    /// Path of the Unix domain socket the daemon listens on — its only door. The
    /// daemon has no network listener by design; remote access goes through the
    /// Orkis.Web gateway, which owns the TCP bind and auth.
    /// </summary>
    public required string SocketPath { get; init; }

    /// <summary>Root directory for run checkpoints.</summary>
    public required string CheckpointDirectory { get; init; }

    /// <summary>Root directory for run event logs.</summary>
    public required string EventDirectory { get; init; }

    /// <summary>Root directory for the durable approval inbox.</summary>
    public required string ApprovalDirectory { get; init; }

    /// <summary>Root directory for the artifact store.</summary>
    public required string ArtifactDirectory { get; init; }

    /// <summary>Root directory for stored schedules.</summary>
    public string ScheduleDirectory { get; init; } = "";

    /// <summary>Persistent workspace shared by the shell and artifact tools.</summary>
    public string WorkspaceKey { get; init; } = "default";

    /// <summary>Use the scripted offline model instead of a live provider.</summary>
    public bool Offline { get; init; }

    /// <summary>
    /// Resolved models, each selectable per run (<c>run --model &lt;key&gt;</c>). Empty
    /// when offline. Built from the config file or legacy environment variables.
    /// </summary>
    public IReadOnlyList<ResolvedModel> Models { get; init; } = [];

    /// <summary>Key (in <see cref="Models"/>) of the model a run uses when it names none.</summary>
    public string? DefaultModelKey { get; init; }

    /// <summary>
    /// The embedding model, or <see langword="null"/> when none is configured — memory
    /// and retrieval stay off without one. Must be OpenAI-kind.
    /// </summary>
    public ResolvedModel? Embedding { get; init; }

    /// <summary>SQLite database for agent memory; used when embeddings are on.</summary>
    public string? MemoryDatabasePath { get; init; }

    /// <summary>Directory of documents to index for search_corpus, or <see langword="null"/>.</summary>
    public string? CorpusDirectory { get; init; }

    /// <summary>SQLite database for the indexed corpus; used when a corpus directory is set.</summary>
    public string? CorpusDatabasePath { get; init; }

    /// <summary>
    /// MCP server to consume: an <c>http(s)://</c> Streamable HTTP endpoint or a stdio
    /// command line. Its tools join the searchable catalogue, and — annotations being
    /// untrusted — pass supervision as mutating.
    /// </summary>
    public string? McpServer { get; init; }

    /// <summary>Isolation sandbox: <c>process</c>, <c>bubblewrap</c>, or <c>firecracker</c>.</summary>
    public string Sandbox { get; init; } = "process";

    /// <summary>Guest kernel image path; required when the sandbox is firecracker.</summary>
    public string? FirecrackerKernelPath { get; init; }

    /// <summary>Guest rootfs image path; required when the sandbox is firecracker.</summary>
    public string? FirecrackerRootfsPath { get; init; }

    /// <summary>Grant firecracker VMs public-internet-only egress (host setup required).</summary>
    public bool FirecrackerEgress { get; init; }
}
