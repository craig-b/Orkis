using Microsoft.Extensions.Options;
using Orkis.Retrieval;

namespace Orkis.Rag.Tests;

public sealed class DirectoryCorpusLoaderTests : IDisposable
{
    private readonly FakeEmbeddingGenerator _embeddings = new();
    private readonly InMemoryVectorStore _store;
    private readonly DirectoryCorpusLoader _loader;
    private readonly string _root = Directory.CreateTempSubdirectory("orkis-corpus-tests-").FullName;

    public DirectoryCorpusLoaderTests()
    {
        _store = new InMemoryVectorStore(_embeddings);
        var parsers = new IDocumentParser[] { new PlainTextParser() };
        _loader = new DirectoryCorpusLoader(
            parsers,
            new DocumentIngestor(
                parsers,
                new TextChunker(Options.Create(new TextChunkerOptions { MaxChunkLength = 80, HardSplitOverlap = 10 })),
                _store
            )
        );
    }

    public void Dispose()
    {
        _embeddings.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task IndexesSupportedFilesKeyedByRelativePath()
    {
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        await File.WriteAllTextAsync(Path.Combine(_root, "cats.txt"), "Cats purr when they are content.");
        await File.WriteAllTextAsync(Path.Combine(_root, "notes", "dogs.md"), "Dogs bark at strangers.");
        await File.WriteAllBytesAsync(Path.Combine(_root, "image.bin"), [1, 2, 3]);

        var (documents, chunks) = await _loader.LoadAsync(_root);

        Assert.Equal(2, documents);
        Assert.True(chunks >= 2);

        var hits = await _store.RetrieveAsync(new RetrievalQuery { Text = "why do dogs bark", TopK = 1 });
        var hit = Assert.Single(hits);
        Assert.Equal("notes/dogs.md", hit.Item.DocumentId);
        Assert.Equal("notes/dogs.md", hit.Item.Metadata["source"]);
    }

    [Fact]
    public async Task ReloadingUpdatesInPlaceInsteadOfDuplicating()
    {
        var path = Path.Combine(_root, "cats.txt");
        await File.WriteAllTextAsync(path, "Cats purr when they are content.");
        await _loader.LoadAsync(_root);

        await File.WriteAllTextAsync(path, "Cats meow at their humans.");
        var (documents, _) = await _loader.LoadAsync(_root);

        Assert.Equal(1, documents);
        var hits = await _store.RetrieveAsync(new RetrievalQuery { Text = "cats", TopK = 5 });
        var hit = Assert.Single(hits);
        Assert.Contains("meow", hit.Item.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipsExtensionsWithoutARegisteredParser()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "page.html"), "<p>hello</p>");

        var (documents, chunks) = await _loader.LoadAsync(_root);

        Assert.Equal(0, documents);
        Assert.Equal(0, chunks);
    }
}
