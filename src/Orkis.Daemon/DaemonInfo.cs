namespace Orkis.Daemon;

/// <summary>Boot-time facts the capabilities endpoint reports alongside the registries.</summary>
internal sealed record DaemonInfo
{
    public required string Sandbox { get; init; }

    public string? DefaultModel { get; init; }

    public required bool MemoryEnabled { get; init; }

    public required bool CorpusEnabled { get; init; }
}
