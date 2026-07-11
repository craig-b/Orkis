namespace Orkis.Tools;

/// <summary>
/// Marks a method as an agent tool. The Orkis source generator emits an
/// <see cref="ITool"/> implementation for it at compile time — JSON schema,
/// argument binding, and invocation — with no reflection at runtime.
/// The containing type must be <see langword="partial"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class OrkisToolAttribute : Attribute
{
    /// <summary>Tool name presented to the model. Defaults to the snake_cased method name.</summary>
    public string? Name { get; set; }

    /// <summary>What the tool does and when to use it, presented to the model.</summary>
    public string? Description { get; set; }

    /// <summary>Declared risk classification, used by supervision policies.</summary>
    public ToolRisk Risk { get; set; } = ToolRisk.Mutating;
}
