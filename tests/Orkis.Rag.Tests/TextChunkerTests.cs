using Microsoft.Extensions.Options;
using Orkis.Retrieval;

namespace Orkis.Rag.Tests;

public sealed class TextChunkerTests
{
    private static TextChunker CreateChunker(int maxLength = 100, int overlap = 5) =>
        new(Options.Create(new TextChunkerOptions { MaxChunkLength = maxLength, HardSplitOverlap = overlap }));

    private static SourceDocument Doc(string text) =>
        new()
        {
            Id = "doc-1",
            Text = text,
            Metadata = new Dictionary<string, string> { ["source"] = "test" },
        };

    [Fact]
    public async Task PacksWholeParagraphsUpToMaxLength()
    {
        var text = string.Join("\n\n", "First paragraph.", "Second paragraph.", "Third paragraph.");

        var chunks = await CreateChunker(maxLength: 40).ChunkAsync(Doc(text));

        Assert.Equal(2, chunks.Count);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", chunks[0].Text);
        Assert.Equal("Third paragraph.", chunks[1].Text);
    }

    [Fact]
    public async Task HardSplitsOversizedParagraphsWithOverlap()
    {
        var text = new string('x', 250);

        var chunks = await CreateChunker(maxLength: 100, overlap: 20).ChunkAsync(Doc(text));

        // Windows step by 80: [0,100) [80,180) [160,250).
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 100));
        Assert.Equal(250 - 160, chunks[2].Text.Length);
    }

    [Fact]
    public async Task AssignsStableIdsDocumentIdAndMetadata()
    {
        var chunks = await CreateChunker(maxLength: 10).ChunkAsync(Doc("one\n\ntwo three four"));

        Assert.All(chunks, c => Assert.Equal("doc-1", c.DocumentId));
        Assert.All(chunks, c => Assert.Equal("test", c.Metadata["source"]));
        Assert.Equal(["doc-1:0000", "doc-1:0001", "doc-1:0002"], chunks.Select(c => c.Id).Take(3).ToList());
    }

    [Fact]
    public async Task EmptyDocumentYieldsNoChunks()
    {
        var chunks = await CreateChunker().ChunkAsync(Doc("   \n\n  "));

        Assert.Empty(chunks);
    }
}
