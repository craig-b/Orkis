using Orkis.Tools;

namespace Orkis.Supervision;

/// <summary>An action the agent wants to take, submitted for supervision before execution.</summary>
public sealed record ProposedAction
{
    /// <summary>The run proposing the action.</summary>
    public required string RunId { get; init; }

    /// <summary>The tool call the agent wants to make.</summary>
    public required ToolCall Call { get; init; }

    /// <summary>Descriptor of the tool being called, including its declared risk.</summary>
    public required ToolDescriptor Tool { get; init; }
}
