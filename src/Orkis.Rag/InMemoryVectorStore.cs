using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Orkis.Retrieval;

/// <summary>
/// Reference chunk store and retriever: embeddings from the host's
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>, cosine similarity over
/// process memory. Suitable for development, testing, and small corpora; contents
/// do not survive a process restart.
/// </summary>
public sealed class InMemoryVectorStore(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
    : IChunkStore,
        IRetriever
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed record Entry(Chunk Chunk, float[] Vector);

    /// <inheritdoc />
    public async Task UpsertAsync(IReadOnlyCollection<Chunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            return;
        }

        var embeddings = await embeddingGenerator
            .GenerateAsync(chunks.Select(static c => c.Text), options: null, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (chunk, embedding) in chunks.Zip(embeddings))
        {
            _entries[chunk.Id] = new Entry(chunk, embedding.Vector.ToArray());
        }
    }

    /// <inheritdoc />
    public Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);

        foreach (var entry in _entries.Values.Where(e => e.Chunk.DocumentId == documentId))
        {
            _entries.TryRemove(entry.Chunk.Id, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Scored<Chunk>>> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);

        var queryEmbeddings = await embeddingGenerator
            .GenerateAsync([query.Text], options: null, cancellationToken)
            .ConfigureAwait(false);
        var queryVector = queryEmbeddings[0].Vector.ToArray();

        return
        [
            .. _entries
                .Values.Where(entry => MatchesFilters(entry.Chunk, query.Filters))
                .Select(entry => new Scored<Chunk>(entry.Chunk, CosineSimilarity(queryVector, entry.Vector)))
                .OrderByDescending(static scored => scored.Score)
                .Take(query.TopK),
        ];
    }

    private static bool MatchesFilters(Chunk chunk, IReadOnlyDictionary<string, string> filters)
    {
        foreach (var (key, value) in filters)
        {
            if (!chunk.Metadata.TryGetValue(key, out var actual) || actual != value)
            {
                return false;
            }
        }

        return true;
    }

    private static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new InvalidOperationException("Embedding dimensions do not match.");
        }

        double dot = 0;
        double normA = 0;
        double normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }
}
