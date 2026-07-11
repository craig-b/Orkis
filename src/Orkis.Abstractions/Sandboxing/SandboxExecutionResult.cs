namespace Orkis.Sandboxing;

/// <summary>The outcome of a sandboxed execution.</summary>
public sealed record SandboxExecutionResult
{
    /// <summary>The process exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Captured standard output.</summary>
    public string StandardOutput { get; init; } = "";

    /// <summary>Captured standard error.</summary>
    public string StandardError { get; init; } = "";

    /// <summary>Whether execution was terminated for exceeding its timeout.</summary>
    public bool TimedOut { get; init; }
}
