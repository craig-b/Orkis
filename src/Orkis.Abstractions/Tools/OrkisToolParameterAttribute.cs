namespace Orkis.Tools;

/// <summary>Describes a tool parameter to the model in the generated JSON schema.</summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class OrkisToolParameterAttribute(string description) : Attribute
{
    /// <summary>The parameter's description, included in the generated schema.</summary>
    public string Description { get; } = description;
}
