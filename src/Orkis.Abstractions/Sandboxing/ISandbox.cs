namespace Orkis.Sandboxing;

/// <summary>Executes untrusted operations in isolation.</summary>
public interface ISandbox
{
    /// <summary>The isolation strength this sandbox provides.</summary>
    SandboxLevel Level { get; }

    /// <summary>Executes the command inside the sandbox.</summary>
    Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default
    );
}
