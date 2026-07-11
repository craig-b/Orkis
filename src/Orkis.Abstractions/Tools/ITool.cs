namespace Orkis.Tools;

/// <summary>A capability the agent can invoke during a run.</summary>
public interface ITool
{
    /// <summary>Describes this tool to the model and to supervision policies.</summary>
    ToolDescriptor Descriptor { get; }

    /// <summary>
    /// Executes the call and returns its result. Implementations should report failures
    /// via <see cref="ToolResult.IsError"/> rather than throwing, so the model can react.
    /// </summary>
    Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default);
}
