using Microsoft.Extensions.Options;
using Orkis.Retrieval;

namespace Orkis.Rag.Tests;

public sealed class SqliteVectorStoreTests : IDisposable
{
    private readonly FakeEmbeddingGenerator _embeddings = new();
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "orkis-tests",
        $"rag-{Guid.NewGuid():n}.db"
    );

    public void Dispose()
    {
        _embeddings.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private SqliteVectorStore CreateStore() =>
        new(_embeddings, Options.Create(new SqliteVectorStoreOptions { DatabasePath = _databasePath }));

    private static Chunk Chunk(string id, string text, string? documentId = null, string? topic = null) =>
        new()
        {
            Id = id,
            Text = text,
            DocumentId = documentId,
            Metadata = topic is null ? new Dictionary<string, string>() : new() { ["topic"] = topic },
        };

    [Fact]
    public async Task RetrievesMostSimilarChunksFirst()
    {
        var store = CreateStore();
        await store.UpsertAsync([
            Chunk("1", "Cats purr when they are content."),
            Chunk("2", "Dogs bark at strangers."),
            Chunk("3", "The stock market closed higher today."),
        ]);

        var results = await store.RetrieveAsync(new RetrievalQuery { Text = "why do cats purr", TopK = 2 });

        Assert.Equal(2, results.Count);
        Assert.Equal("1", results[0].Item.Id);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task CorpusSurvivesANewStoreInstance()
    {
        await CreateStore().UpsertAsync([Chunk("1", "Cats purr when they are content.", topic: "cats")]);

        var results = await CreateStore().RetrieveAsync(new RetrievalQuery { Text = "purring cats" });

        var single = Assert.Single(results);
        Assert.Equal("1", single.Item.Id);
        Assert.Equal("cats", single.Item.Metadata["topic"]);
    }

    [Fact]
    public async Task UpsertReplacesChunksWithTheSameId()
    {
        var store = CreateStore();
        await store.UpsertAsync([Chunk("1", "Original text about cats.")]);
        await store.UpsertAsync([Chunk("1", "Replacement text about dogs.")]);

        var results = await store.RetrieveAsync(new RetrievalQuery { Text = "dogs" });

        var single = Assert.Single(results);
        Assert.Contains("Replacement", single.Item.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteDocumentRemovesOnlyItsChunks()
    {
        var store = CreateStore();
        await store.UpsertAsync([
            Chunk("1", "Cats purr.", documentId: "doc-a"),
            Chunk("2", "Cats sleep.", documentId: "doc-a"),
            Chunk("3", "Dogs bark.", documentId: "doc-b"),
        ]);

        await store.DeleteDocumentAsync("doc-a");
        var results = await store.RetrieveAsync(new RetrievalQuery { Text = "animals", TopK = 10 });

        Assert.Equal("3", Assert.Single(results).Item.Id);
    }

    [Fact]
    public async Task FiltersRestrictResults()
    {
        var store = CreateStore();
        await store.UpsertAsync([
            Chunk("1", "Cats purr loudly.", topic: "cats"),
            Chunk("2", "Cats and dogs both purr?", topic: "dogs"),
        ]);

        var results = await store.RetrieveAsync(
            new RetrievalQuery
            {
                Text = "purring",
                TopK = 10,
                Filters = new Dictionary<string, string> { ["topic"] = "dogs" },
            }
        );

        Assert.Equal("2", Assert.Single(results).Item.Id);
    }
}
