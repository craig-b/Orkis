using System.Text;
using Orkis.Retrieval;

namespace Orkis.Rag.Tests;

public sealed class HtmlDocumentParserTests
{
    private readonly HtmlDocumentParser _parser = new();

    private static MemoryStream Html(string markup) => new(Encoding.UTF8.GetBytes(markup));

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("TEXT/HTML", true)]
    [InlineData("application/xhtml+xml", true)]
    [InlineData("application/pdf", false)]
    [InlineData("text/plain", false)]
    public void CanParseMatchesHtmlContentTypes(string contentType, bool expected) =>
        Assert.Equal(expected, _parser.CanParse(contentType));

    [Fact]
    public async Task ExtractsBlockTextAndDropsScriptStyleNoise()
    {
        using var content = Html(
            """
            <html><head><title> Cat Facts </title><style>p { color: red; }</style></head>
            <body>
              <h1>About cats</h1>
              <p>Cats   purr when
              they are content.</p>
              <script>alert("never this");</script>
              <p>Cats sleep a lot.</p>
            </body></html>
            """
        );

        var document = await _parser.ParseAsync(content, "text/html");

        Assert.Equal("About cats\n\nCats purr when they are content.\n\nCats sleep a lot.", document.Text);
        Assert.Equal("Cat Facts", document.Metadata["title"]);
    }

    [Fact]
    public async Task NestedBlockElementsAreNotDuplicated()
    {
        using var content = Html("<ul><li><p>only once</p></li></ul>");

        var document = await _parser.ParseAsync(content, "text/html");

        Assert.Equal("only once", document.Text);
    }

    [Fact]
    public async Task MarkupWithoutBlockElementsFallsBackToBodyText()
    {
        using var content = Html("<html><body>just <b>inline</b> text</body></html>");

        var document = await _parser.ParseAsync(content, "text/html");

        Assert.Equal("just inline text", document.Text);
        Assert.False(document.Metadata.ContainsKey("title"));
    }
}
