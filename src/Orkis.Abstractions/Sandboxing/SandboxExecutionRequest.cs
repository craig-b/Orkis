namespace Orkis.Sandboxing;

/// <summary>A command to execute inside a sandbox.</summary>
public sealed record SandboxExecutionRequest
{
    /// <summary>The executable to run.</summary>
    public required string Executable { get; init; }

    /// <summary>Arguments passed to the executable.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Working directory inside the sandbox, or <see langword="null"/> for the sandbox default.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Key of the workload-scoped persistent storage this execution should run in, or
    /// <see langword="null"/> for a throwaway scratch deleted after the execution.
    /// Executions sharing a key on the same sandbox implementation see the same files;
    /// storage is per implementation and never crosses isolation levels — files move
    /// between levels only by explicit artifact promotion.
    /// </summary>
    public string? WorkspaceKey { get; init; }

    /// <summary>Maximum execution time before the sandbox terminates the command.</summary>
    public TimeSpan? Timeout { get; init; }
}
