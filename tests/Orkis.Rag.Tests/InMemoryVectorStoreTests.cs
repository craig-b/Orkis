using Orkis.Retrieval;

namespace Orkis.Rag.Tests;

public sealed class InMemoryVectorStoreTests : IDisposable
{
    private readonly FakeEmbeddingGenerator _embeddings = new();
    private readonly InMemoryVectorStore _store;

    public InMemoryVectorStoreTests() => _store = new InMemoryVectorStore(_embeddings);

    public void Dispose() => _embeddings.Dispose();

    private static Chunk Chunk(string id, string text, string? documentId = null, string? topic = null) =>
        new()
        {
            Id = id,
            DocumentId = documentId,
            Text = text,
            Metadata = topic is null
                ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
                : new Dictionary<string, string> { ["topic"] = topic },
        };

    [Fact]
    public async Task RetrievesMostRelevantChunkFirst()
    {
        await _store.UpsertAsync([Chunk("1", "cats purr softly"), Chunk("2", "dogs bark loudly")]);

        var results = await _store.RetrieveAsync(new RetrievalQuery { Text = "do cats purr" });

        Assert.Equal(2, results.Count);
        Assert.Equal("1", results[0].Item.Id);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task RespectsTopK()
    {
        await _store.UpsertAsync([Chunk("1", "alpha"), Chunk("2", "beta"), Chunk("3", "gamma")]);

        var results = await _store.RetrieveAsync(new RetrievalQuery { Text = "alpha", TopK = 2 });

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task AppliesMetadataFilters()
    {
        await _store.UpsertAsync([Chunk("1", "shared text", topic: "a"), Chunk("2", "shared text", topic: "b")]);

        var results = await _store.RetrieveAsync(
            new RetrievalQuery
            {
                Text = "shared",
                Filters = new Dictionary<string, string> { ["topic"] = "b" },
            }
        );

        var single = Assert.Single(results);
        Assert.Equal("2", single.Item.Id);
    }

    [Fact]
    public async Task DeletesAllChunksOfADocument()
    {
        await _store.UpsertAsync([
            Chunk("1", "first", documentId: "doc-a"),
            Chunk("2", "second", documentId: "doc-a"),
            Chunk("3", "third", documentId: "doc-b"),
        ]);

        await _store.DeleteDocumentAsync("doc-a");

        var results = await _store.RetrieveAsync(new RetrievalQuery { Text = "anything", TopK = 10 });
        var single = Assert.Single(results);
        Assert.Equal("3", single.Item.Id);
    }

    [Fact]
    public async Task UpsertReplacesChunkWithSameId()
    {
        await _store.UpsertAsync([Chunk("1", "original text")]);
        await _store.UpsertAsync([Chunk("1", "replacement text")]);

        var results = await _store.RetrieveAsync(new RetrievalQuery { Text = "replacement", TopK = 10 });

        var single = Assert.Single(results);
        Assert.Equal("replacement text", single.Item.Text);
    }
}
