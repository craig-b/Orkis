namespace Orkis.Agents;

/// <summary>The outcome of an agent run segment: either terminal, or paused awaiting supervision.</summary>
public sealed record AgentRunResult
{
    /// <summary>The run's identifier, usable to resume a paused run.</summary>
    public required string RunId { get; init; }

    /// <summary>The run's status when this segment ended.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>The last assistant response text, if any.</summary>
    public string? FinalText { get; init; }

    /// <summary>Resources the run has consumed so far.</summary>
    public required RunUsage Usage { get; init; }
}
