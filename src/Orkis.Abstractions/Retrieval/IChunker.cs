namespace Orkis.Retrieval;

/// <summary>Splits a parsed document into chunks sized for embedding and retrieval.</summary>
public interface IChunker
{
    /// <summary>Splits the document into chunks, in document order.</summary>
    Task<IReadOnlyList<Chunk>> ChunkAsync(SourceDocument document, CancellationToken cancellationToken = default);
}
