using System.Text;
using Microsoft.Extensions.Options;
using Orkis.Retrieval;

namespace Orkis.Rag.Tests;

public sealed class DocumentIngestorTests : IDisposable
{
    private readonly FakeEmbeddingGenerator _embeddings = new();
    private readonly InMemoryVectorStore _store;
    private readonly DocumentIngestor _ingestor;

    public DocumentIngestorTests()
    {
        _store = new InMemoryVectorStore(_embeddings);
        _ingestor = new DocumentIngestor(
            [new PlainTextParser()],
            new TextChunker(Options.Create(new TextChunkerOptions { MaxChunkLength = 50, HardSplitOverlap = 10 })),
            _store
        );
    }

    public void Dispose() => _embeddings.Dispose();

    [Fact]
    public async Task IngestsStreamEndToEndAndMakesContentRetrievable()
    {
        var text = "Cats purr when they are content.\n\nDogs bark at strangers.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        var written = await _ingestor.IngestAsync(stream, "text/plain");

        Assert.Equal(2, written);
        var results = await _store.RetrieveAsync(new RetrievalQuery { Text = "cats purr", TopK = 1 });
        Assert.Contains("purr", Assert.Single(results).Item.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUnsupportedContentType()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<NotSupportedException>(() => _ingestor.IngestAsync(stream, "application/pdf"));
    }
}
