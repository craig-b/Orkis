using System.Text.Json;

namespace Orkis.Tools;

/// <summary>A model-requested invocation of a tool. Serializable for checkpointing.</summary>
public sealed record ToolCall
{
    /// <summary>Identifier correlating this call with its <see cref="ToolResult"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Name of the tool to invoke.</summary>
    public required string ToolName { get; init; }

    /// <summary>The call's arguments as a JSON object.</summary>
    public required JsonElement Arguments { get; init; }
}
