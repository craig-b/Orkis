using Orkis.Retrieval;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Orkis.Rag.Tests;

public sealed class PdfDocumentParserTests
{
    private readonly PdfDocumentParser _parser = new();

    /// <summary>Builds a real PDF in memory, so no binary fixture lives in the repo.</summary>
    private static MemoryStream Pdf(params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var pageText in pageTexts)
        {
            var page = builder.AddPage(PageSize.A4);
            if (pageText.Length > 0)
            {
                page.AddText(pageText, 12, new PdfPoint(25, 700), font);
            }
        }

        return new MemoryStream(builder.Build());
    }

    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("APPLICATION/PDF", true)]
    [InlineData("text/html", false)]
    public void CanParseMatchesPdfContentType(string contentType, bool expected) =>
        Assert.Equal(expected, _parser.CanParse(contentType));

    [Fact]
    public async Task ExtractsTextFromEveryPageWithMetadata()
    {
        using var content = Pdf("Cats purr when they are content.", "Dogs bark at strangers.");

        var document = await _parser.ParseAsync(content, "application/pdf");

        Assert.Equal(
            "Cats purr when they are content.\n\nDogs bark at strangers.",
            document.Text.ReplaceLineEndings("\n")
        );
        Assert.Equal("2", document.Metadata["pages"]);
    }

    [Fact]
    public async Task PdfWithNoEmbeddedTextYieldsAnEmptyDocument()
    {
        // An image-only scan has no text layer; that is a legitimate empty result,
        // not an error — OCR is a separate capability.
        using var content = Pdf("");

        var document = await _parser.ParseAsync(content, "application/pdf");

        Assert.Equal("", document.Text);
        Assert.Equal("1", document.Metadata["pages"]);
    }
}
