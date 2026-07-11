namespace Orkis.Artifacts;

/// <summary>
/// Trusted storage for curated outputs promoted out of sandbox workspaces. Artifacts
/// are the only way files cross isolation levels: sandbox storage never moves between
/// levels, but a supervised promotion lifts a file into this store, and staging copies
/// one back into a workspace. Artifacts are immutable once written — promotion is a
/// trust decision about specific content, so a name can never be silently rebound.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="name"/>. Throws
    /// <see cref="InvalidOperationException"/> when the artifact already exists.
    /// </summary>
    Task<ArtifactInfo> SaveAsync(string name, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the artifact's content for reading, or <see langword="null"/> when no such
    /// artifact exists. The caller disposes the stream.
    /// </summary>
    Task<Stream?> OpenAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>All stored artifacts, oldest first.</summary>
    Task<IReadOnlyList<ArtifactInfo>> ListAsync(CancellationToken cancellationToken = default);
}
