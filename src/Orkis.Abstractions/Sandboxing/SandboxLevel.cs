namespace Orkis.Sandboxing;

/// <summary>
/// Graduated isolation strength for executing untrusted operations.
/// Higher values are strictly stronger; policies compare levels ordinally.
/// </summary>
public enum SandboxLevel
{
    /// <summary>No isolation: runs with the host process's privileges.</summary>
    None = 0,

    /// <summary>Standard isolation: a separate constrained execution context (e.g. a restricted process).</summary>
    Standard = 1,

    /// <summary>Strict isolation: a fully separated environment (e.g. a container or micro-VM).</summary>
    Strict = 2,
}
