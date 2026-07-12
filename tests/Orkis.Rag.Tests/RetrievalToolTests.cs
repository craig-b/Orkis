using System.Text.Json;
using Orkis.Retrieval;
using Orkis.Tools;

namespace Orkis.Rag.Tests;

public sealed class RetrievalToolTests : IDisposable
{
    private readonly FakeEmbeddingGenerator _embeddings = new();
    private readonly InMemoryVectorStore _store;

    public RetrievalToolTests()
    {
        _store = new InMemoryVectorStore(_embeddings);
    }

    public void Dispose() => _embeddings.Dispose();

    private async Task SeedAsync() =>
        await _store.UpsertAsync([
            new Chunk
            {
                Id = "c1",
                Text = "Cats purr when they are content.",
                Metadata = new Dictionary<string, string> { ["source"] = "cats.md" },
            },
            new Chunk { Id = "c2", Text = "Dogs bark at strangers." },
            new Chunk { Id = "c3", Text = "The stock market closed higher today." },
        ]);

    private static ToolCall Call(string argumentsJson) =>
        new()
        {
            Id = "call-1",
            ToolName = "search_corpus",
            Arguments = JsonSerializer.Deserialize<JsonElement>(argumentsJson),
        };

    [Fact]
    public async Task ReturnsRelevantPassagesWithIdsAndMetadata()
    {
        await SeedAsync();
        var tool = new RetrievalTool(_store);

        var result = await tool.InvokeAsync(Call("""{"query":"why do cats purr","top_k":2}"""));

        Assert.False(result.IsError);
        Assert.Contains("[c1]", result.Content, StringComparison.Ordinal);
        Assert.Contains("source=cats.md", result.Content, StringComparison.Ordinal);
        Assert.Contains("Cats purr", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RerankerReordersAndNarrowsAWideFirstStage()
    {
        var retriever = new StubRetriever(
            new Scored<Chunk>(new Chunk { Id = "first", Text = "a" }, 0.9),
            new Scored<Chunk>(new Chunk { Id = "second", Text = "b" }, 0.8)
        );
        // A "reranker" that inverts first-stage order, proving both that it runs and
        // that its ordering (not the retriever's) decides the final result.
        var tool = new RetrievalTool(retriever, new InvertingReranker());

        var result = await tool.InvokeAsync(Call("""{"query":"q","top_k":1}"""));

        Assert.False(result.IsError);
        Assert.Equal(20, retriever.LastTopK); // Wide first stage feeds the reranker.
        Assert.Contains("[second]", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("[first]", result.Content, StringComparison.Ordinal);
    }

    private sealed class StubRetriever(params Scored<Chunk>[] results) : IRetriever
    {
        public int LastTopK { get; private set; }

        public Task<IReadOnlyList<Scored<Chunk>>> RetrieveAsync(
            RetrievalQuery query,
            CancellationToken cancellationToken = default
        )
        {
            LastTopK = query.TopK;
            return Task.FromResult<IReadOnlyList<Scored<Chunk>>>(results);
        }
    }

    [Fact]
    public async Task EmptyCorpusYieldsAnHonestNoResultsMessage()
    {
        var tool = new RetrievalTool(_store);

        var result = await tool.InvokeAsync(Call("""{"query":"anything"}"""));

        Assert.False(result.IsError);
        Assert.Contains("No matching content", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingQueryIsAnError()
    {
        var tool = new RetrievalTool(_store);

        var result = await tool.InvokeAsync(Call("{}"));

        Assert.True(result.IsError);
    }

    [Fact]
    public void DeclaredReadOnly() => Assert.Equal(ToolRisk.ReadOnly, new RetrievalTool(_store).Descriptor.Risk);

    private sealed class InvertingReranker : IReranker
    {
        public Task<IReadOnlyList<Scored<Chunk>>> RerankAsync(
            string query,
            IReadOnlyList<Scored<Chunk>> candidates,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult<IReadOnlyList<Scored<Chunk>>>([
                .. candidates.Select((c, i) => new Scored<Chunk>(c.Item, i)).OrderByDescending(static s => s.Score),
            ]);
    }
}
