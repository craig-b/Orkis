namespace Orkis.Runs;

/// <summary>
/// Per-run resource limits. A <see langword="null"/> limit means unlimited.
/// The run stops with a budget-exceeded outcome when any limit is reached.
/// </summary>
public sealed record RunBudget
{
    /// <summary>A budget with no limits.</summary>
    public static RunBudget Unlimited { get; } = new();

    /// <summary>Maximum total tokens (input + output) the run may consume.</summary>
    public long? MaxTokens { get; init; }

    /// <summary>Maximum model spend, in the configured billing currency.</summary>
    public decimal? MaxCost { get; init; }

    /// <summary>Maximum wall-clock duration of the run, excluding time paused for supervision.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>Maximum number of tool calls the run may make.</summary>
    public int? MaxToolCalls { get; init; }
}
