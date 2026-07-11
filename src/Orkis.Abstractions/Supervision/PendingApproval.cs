using Orkis.Tools;

namespace Orkis.Supervision;

/// <summary>
/// A proposed action recorded in an approval queue, awaiting an out-of-band decision.
/// Identified by its run id and tool call id. Serializable, so an inbox can outlive
/// the process that submitted it.
/// </summary>
public sealed record PendingApproval
{
    /// <summary>The run whose action awaits a decision.</summary>
    public required string RunId { get; init; }

    /// <summary>The tool call the agent wants to make.</summary>
    public required ToolCall Call { get; init; }

    /// <summary>Descriptor of the tool being called, including its declared risk.</summary>
    public required ToolDescriptor Tool { get; init; }

    /// <summary>When the approval was requested.</summary>
    public required DateTimeOffset RequestedAt { get; init; }
}
