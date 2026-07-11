using System.Text.Json;

namespace Orkis.Tools;

/// <summary>Describes a tool to the model and to supervision policies.</summary>
public sealed record ToolDescriptor
{
    /// <summary>The tool's unique name, as presented to the model.</summary>
    public required string Name { get; init; }

    /// <summary>What the tool does and when to use it, as presented to the model.</summary>
    public required string Description { get; init; }

    /// <summary>JSON Schema describing the tool's parameters.</summary>
    public required JsonElement ParametersSchema { get; init; }

    /// <summary>
    /// The tool's declared risk classification. Defaults to <see cref="ToolRisk.Mutating"/>
    /// so that a tool must explicitly opt in to being treated as safe.
    /// </summary>
    public ToolRisk Risk { get; init; } = ToolRisk.Mutating;
}
