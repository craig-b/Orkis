using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Orkis.Retrieval;

/// <summary>
/// A chunk store and retriever persisted in a single SQLite file: embeddings from the
/// host's <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> stored as float32
/// blobs, cosine similarity computed with a full scan at query time. This is durable
/// storage, not approximate-nearest-neighbor indexing — entirely adequate into the
/// tens of thousands of chunks; beyond that, a vector-native backend (pgvector,
/// Qdrant) is the intended upgrade path behind the same interfaces.
/// </summary>
public sealed class SqliteVectorStore : IChunkStore, IRetriever
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly string _connectionString;
    private readonly Lock _schemaGate = new();
    private volatile bool _schemaReady;

    public SqliteVectorStore(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IOptions<SqliteVectorStoreOptions> options
    )
    {
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        ArgumentNullException.ThrowIfNull(options);

        _embeddingGenerator = embeddingGenerator;
        var databasePath = Path.GetFullPath(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    /// <inheritdoc />
    public async Task UpsertAsync(IReadOnlyCollection<Chunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            return;
        }

        var embeddings = await _embeddingGenerator
            .GenerateAsync(chunks.Select(static c => c.Text), options: null, cancellationToken)
            .ConfigureAwait(false);

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = (SqliteTransaction)
                await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (var (chunk, embedding) in chunks.Zip(embeddings))
                {
                    var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT OR REPLACE INTO chunks (id, document_id, text, metadata, embedding)
                        VALUES (@id, @documentId, @text, @metadata, @embedding)
                        """;
                    command.Parameters.AddWithValue("@id", chunk.Id);
                    command.Parameters.AddWithValue("@documentId", (object?)chunk.DocumentId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@text", chunk.Text);
                    command.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(chunk.Metadata, JsonOptions));
                    command.Parameters.AddWithValue(
                        "@embedding",
                        MemoryMarshal.AsBytes(embedding.Vector.Span).ToArray()
                    );
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM chunks WHERE document_id = @documentId";
            command.Parameters.AddWithValue("@documentId", documentId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Scored<Chunk>>> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);

        var queryEmbeddings = await _embeddingGenerator
            .GenerateAsync([query.Text], options: null, cancellationToken)
            .ConfigureAwait(false);
        var queryVector = queryEmbeddings[0].Vector.ToArray();

        var scored = new List<Scored<Chunk>>();
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT id, document_id, text, metadata, embedding FROM chunks";
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var metadata =
                        JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3), JsonOptions) ?? [];
                    var chunk = new Chunk
                    {
                        Id = reader.GetString(0),
                        DocumentId = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Text = reader.GetString(2),
                        Metadata = metadata,
                    };

                    if (!MatchesFilters(chunk, query.Filters))
                    {
                        continue;
                    }

                    var vector = MemoryMarshal.Cast<byte, float>((byte[])reader.GetValue(4));
                    scored.Add(new Scored<Chunk>(chunk, CosineSimilarity(queryVector, vector)));
                }
            }
        }

        return [.. scored.OrderByDescending(static s => s.Score).Take(query.TopK)];
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!_schemaReady)
        {
            lock (_schemaGate)
            {
                if (!_schemaReady)
                {
                    var command = connection.CreateCommand();
                    command.CommandText = """
                        CREATE TABLE IF NOT EXISTS chunks (
                            id TEXT PRIMARY KEY,
                            document_id TEXT,
                            text TEXT NOT NULL,
                            metadata TEXT NOT NULL,
                            embedding BLOB NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id);
                        """;
                    command.ExecuteNonQuery();
                    _schemaReady = true;
                }
            }
        }

        return connection;
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
