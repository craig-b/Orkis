namespace Orkis.Retrieval;

/// <summary>
/// Ingests every supported file under a directory. Documents are keyed by relative
/// path, so re-loading after edits updates chunks in place (chunk ids derive from the
/// document id); a file that shrank can leave stale tail chunks — delete the index to
/// fully rebuild. Files whose extension has no registered parser are skipped.
/// </summary>
public sealed class DirectoryCorpusLoader(IEnumerable<IDocumentParser> parsers, DocumentIngestor ingestor)
{
    private static readonly Dictionary<string, string> ContentTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".pdf"] = "application/pdf",
    };

    /// <summary>Ingests the directory tree, returning how many documents and chunks were written.</summary>
    public async Task<(int Documents, int Chunks)> LoadAsync(
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        var root = Path.GetFullPath(directory);
        var documents = 0;
        var chunks = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!ContentTypesByExtension.TryGetValue(Path.GetExtension(path), out var contentType))
            {
                continue;
            }

            var parser = parsers.FirstOrDefault(p => p.CanParse(contentType));
            if (parser is null)
            {
                // e.g. a .pdf without the PDF parser package registered.
                continue;
            }

            SourceDocument parsed;
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                parsed = await parser.ParseAsync(stream, contentType, cancellationToken).ConfigureAwait(false);
            }

            // Relative path as the id makes ingestion idempotent and gives retrieval
            // results a human-meaningful source to cite.
            var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            var metadata = new Dictionary<string, string>(parsed.Metadata, StringComparer.Ordinal)
            {
                ["source"] = relativePath,
            };
            chunks += await ingestor
                .IngestAsync(parsed with { Id = relativePath, Metadata = metadata }, cancellationToken)
                .ConfigureAwait(false);
            documents++;
        }

        return (documents, chunks);
    }
}
