using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Orkis.Memory;

/// <summary>
/// Agent memories persisted in a single SQLite file: embeddings from the host's
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> stored as float32 blobs,
/// cosine similarity with a full scan at query time — the
/// <c>Orkis.Rag.Sqlite</c> pattern applied to memories, with scope as a first-class
/// column. Searches return one scope plus the global scope.
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly string _connectionString;
    private readonly Lock _schemaGate = new();
    private volatile bool _schemaReady;

    public SqliteMemoryStore(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IOptions<SqliteMemoryStoreOptions> options
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
    public async Task WriteAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var embeddings = await _embeddingGenerator
            .GenerateAsync([entry.Text], options: null, cancellationToken)
            .ConfigureAwait(false);

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO memories (id, scope, text, created_at, metadata, embedding)
                VALUES (@id, @scope, @text, @createdAt, @metadata, @embedding)
                """;
            command.Parameters.AddWithValue("@id", entry.Id);
            command.Parameters.AddWithValue("@scope", entry.Scope);
            command.Parameters.AddWithValue("@text", entry.Text);
            command.Parameters.AddWithValue("@createdAt", entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(entry.Metadata, JsonOptions));
            command.Parameters.AddWithValue("@embedding", MemoryMarshal.AsBytes(embeddings[0].Vector.Span).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Scored<MemoryEntry>>> SearchAsync(
        string query,
        string scope = MemoryScopes.Global,
        int topK = 8,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrEmpty(scope);

        var queryEmbeddings = await _embeddingGenerator
            .GenerateAsync([query], options: null, cancellationToken)
            .ConfigureAwait(false);
        var queryVector = queryEmbeddings[0].Vector.ToArray();

        var scored = new List<Scored<MemoryEntry>>();
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, scope, text, created_at, metadata, embedding
                FROM memories WHERE scope = @scope OR scope = @global
                """;
            command.Parameters.AddWithValue("@scope", scope);
            command.Parameters.AddWithValue("@global", MemoryScopes.Global);

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var entry = new MemoryEntry
                    {
                        Id = reader.GetString(0),
                        Scope = reader.GetString(1),
                        Text = reader.GetString(2),
                        CreatedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                        Metadata =
                            JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4), JsonOptions)
                            ?? [],
                    };

                    var vector = MemoryMarshal.Cast<byte, float>((byte[])reader.GetValue(5));
                    scored.Add(new Scored<MemoryEntry>(entry, CosineSimilarity(queryVector, vector)));
                }
            }
        }

        return [.. scored.OrderByDescending(static s => s.Score).Take(topK)];
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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
                        CREATE TABLE IF NOT EXISTS memories (
                            id TEXT PRIMARY KEY,
                            scope TEXT NOT NULL,
                            text TEXT NOT NULL,
                            created_at TEXT NOT NULL,
                            metadata TEXT NOT NULL,
                            embedding BLOB NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS idx_memories_scope ON memories(scope);
                        """;
                    command.ExecuteNonQuery();
                    _schemaReady = true;
                }
            }
        }

        return connection;
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
