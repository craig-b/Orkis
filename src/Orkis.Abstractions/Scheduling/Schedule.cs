namespace Orkis.Scheduling;

/// <summary>
/// A cron-triggered run template. Each firing is a fresh run (fresh transcript), so an
/// unbounded transcript never accumulates; continuity across firings is storage and an
/// optional handoff note, per <see cref="ScheduleContinuity"/>.
/// </summary>
public sealed record Schedule
{
    /// <summary>Stable identifier; also the storage/memory scope key (<c>sched-&lt;id&gt;</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name for lists and logs.</summary>
    public required string Name { get; init; }

    /// <summary>The cron expression governing firings (Cronos syntax; seconds optional).</summary>
    public required string Cron { get; init; }

    /// <summary>The prompt each firing runs.</summary>
    public required string Prompt { get; init; }

    /// <summary>Supervisor key for firings; approvals park (and notify) under <c>queue</c>.</summary>
    public string SupervisorKey { get; init; } = "queue";

    /// <summary>Registered model key, or <see langword="null"/> for the daemon default.</summary>
    public string? ModelKey { get; init; }

    /// <summary>
    /// Restricts firings to these tool names, or <see langword="null"/> for all —
    /// autonomy bounded by capability (e.g. yolo with read-only tools).
    /// </summary>
    public IReadOnlyList<string>? ToolNames { get; init; }

    /// <summary>How much a firing carries forward from the previous one.</summary>
    public ScheduleContinuity Continuity { get; init; } = ScheduleContinuity.Fresh;

    /// <summary>Token budget per firing, or <see langword="null"/> for unlimited.</summary>
    public long? MaxTokens { get; init; }

    /// <summary>Whether firings occur; a paused schedule is kept but not triggered.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The closing note from the most recent firing, injected into the next when
    /// continuity is <see cref="ScheduleContinuity.SharedStorageWithHandoff"/>.</summary>
    public string? Handoff { get; init; }

    /// <summary>When the schedule last fired, or <see langword="null"/> if never.</summary>
    public DateTimeOffset? LastFiredAt { get; init; }

    /// <summary>The run id of the most recent firing, if any.</summary>
    public string? LastRunId { get; init; }
}

/// <summary>How much of the previous firing a scheduled run inherits.</summary>
public enum ScheduleContinuity
{
    /// <summary>Nothing carried: a clean run each firing.</summary>
    Fresh = 0,

    /// <summary>Fresh transcript, but a persistent workspace and memory scope shared across firings.</summary>
    SharedStorage = 1,

    /// <summary>Shared storage plus the previous firing's closing note injected into the prompt.</summary>
    SharedStorageWithHandoff = 2,
}
