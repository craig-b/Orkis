using System.Collections.ObjectModel;

namespace Orkis.Agents;

/// <summary>Resources consumed by a run so far.</summary>
public sealed record RunUsage
{
    /// <summary>Total input tokens consumed across all model calls.</summary>
    public long InputTokens { get; init; }

    /// <summary>Total output tokens produced across all model calls.</summary>
    public long OutputTokens { get; init; }

    /// <summary>
    /// Total cost per the host's <see cref="Orkis.Runs.ICostCalculator"/>; zero when
    /// no pricing is configured.
    /// </summary>
    public decimal Cost { get; init; }

    /// <summary>Provider-specific token buckets accumulated across all model calls.</summary>
    public IReadOnlyDictionary<string, long> AdditionalTokenCounts { get; init; } =
        ReadOnlyDictionary<string, long>.Empty;

    /// <summary>Number of tool calls executed (denied and rejected calls are not counted).</summary>
    public int ToolCalls { get; init; }

    /// <summary>Wall-clock time spent actively running, excluding time paused for supervision.</summary>
    public TimeSpan ActiveDuration { get; init; }
}
