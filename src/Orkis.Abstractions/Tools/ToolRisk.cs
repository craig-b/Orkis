namespace Orkis.Tools;

/// <summary>
/// A tool's self-declared risk classification, used by supervisors when deciding
/// whether an invocation needs approval and how strongly it must be sandboxed.
/// </summary>
public enum ToolRisk
{
    /// <summary>Observes state without changing it (searches, reads, lookups).</summary>
    ReadOnly = 0,

    /// <summary>Changes state in ways that are bounded and generally reversible.</summary>
    Mutating = 1,

    /// <summary>Can cause irreversible or far-reaching effects (deletion, code execution, external side effects).</summary>
    Destructive = 2,
}
