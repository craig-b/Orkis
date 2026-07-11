namespace Orkis.Retrieval;

/// <summary>The ingestion pipeline: parse (when needed), chunk, embed, and index a document.</summary>
public sealed class DocumentIngestor(IEnumerable<IDocumentParser> parsers, IChunker chunker, IChunkStore store)
{
    /// <summary>Chunks and indexes an already-parsed document, returning the number of chunks written.</summary>
    public async Task<int> IngestAsync(SourceDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var chunks = await chunker.ChunkAsync(document, cancellationToken).ConfigureAwait(false);
        await store.UpsertAsync([.. chunks], cancellationToken).ConfigureAwait(false);
        return chunks.Count;
    }

    /// <summary>Parses, chunks, and indexes raw content, returning the number of chunks written.</summary>
    public async Task<int> IngestAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        var parser =
            parsers.FirstOrDefault(p => p.CanParse(contentType))
            ?? throw new NotSupportedException($"No registered document parser handles '{contentType}'.");

        var document = await parser.ParseAsync(content, contentType, cancellationToken).ConfigureAwait(false);
        return await IngestAsync(document, cancellationToken).ConfigureAwait(false);
    }
}
