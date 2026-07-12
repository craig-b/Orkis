using Orkis.Agents;

namespace Orkis.Runs;

/// <summary>
/// A run's observable state as of its latest checkpoint — what a registry lists and a
/// UI shows, without exposing the run's full internal state.
/// </summary>
public sealed record RunSummary
{
    /// <summary>The run's identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>The run's status at the latest checkpoint.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>Key of the supervisor governing the run.</summary>
    public required string SupervisorKey { get; init; }

    /// <summary>Whether the run is a chat (turns end awaiting the user).</summary>
    public bool Conversational { get; init; }

    /// <summary>Total input tokens consumed so far.</summary>
    public long InputTokens { get; init; }

    /// <summary>Total output tokens produced so far.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Accumulated cost so far, per the host's cost calculator.</summary>
    public decimal Cost { get; init; }

    /// <summary>Number of tool calls executed so far.</summary>
    public int ToolCalls { get; init; }

    /// <summary>When the latest checkpoint was written.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
