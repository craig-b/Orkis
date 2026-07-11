namespace Orkis.Retrieval;

/// <summary>
/// Writable side of a chunk index. Implementations typically also implement
/// <see cref="IRetriever"/> over the same backing store.
/// </summary>
public interface IChunkStore
{
    /// <summary>Adds the chunks to the index, replacing any existing chunks with the same ids.</summary>
    Task UpsertAsync(IReadOnlyCollection<Chunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>Removes all chunks belonging to the given source document.</summary>
    Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default);
}
