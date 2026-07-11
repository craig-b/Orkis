namespace Orkis.Sandboxing;

/// <summary>
/// Optional sandbox capability: orchestrator-side access to files in a workload's
/// persistent workspace without executing anything inside the sandbox. This is the
/// transfer mechanism behind artifact promotion and staging — sandboxed code never
/// touches the artifact store or its credentials; the orchestrator moves the bytes.
/// </summary>
public interface IWorkspaceFileAccess
{
    /// <summary>
    /// Opens a workspace file for reading, or <see langword="null"/> when the file (or
    /// the workspace itself) does not exist. <paramref name="relativePath"/> is relative
    /// to the workspace root and must not escape it. The caller disposes the stream.
    /// </summary>
    Task<Stream?> ReadWorkspaceFileAsync(
        string workspaceKey,
        string relativePath,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Writes a file into the workspace, creating it (and parent directories) as
    /// needed and replacing any existing file at that path.
    /// </summary>
    Task WriteWorkspaceFileAsync(
        string workspaceKey,
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default
    );
}
