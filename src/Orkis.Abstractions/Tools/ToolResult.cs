namespace Orkis.Tools;

/// <summary>The outcome of a tool invocation. Serializable for checkpointing.</summary>
public sealed record ToolResult
{
    /// <summary>Identifier of the <see cref="ToolCall"/> this result answers.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>The result content returned to the model.</summary>
    public required string Content { get; init; }

    /// <summary>Whether the invocation failed; failed results are still returned to the model.</summary>
    public bool IsError { get; init; }
}
