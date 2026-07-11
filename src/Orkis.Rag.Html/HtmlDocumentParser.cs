using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Orkis.Retrieval;

/// <summary>
/// Parses HTML into a <see cref="SourceDocument"/> using AngleSharp's HTML5-compliant
/// parser: script/style/template noise is dropped, and block-level elements become
/// blank-line-separated paragraphs so paragraph-aware chunking has real boundaries to
/// work with. The page title, when present, is carried as document metadata.
/// </summary>
public sealed class HtmlDocumentParser : IDocumentParser
{
    private static readonly string[] SupportedContentTypes = ["text/html", "application/xhtml+xml"];

    private const string NoiseSelector = "script, style, noscript, template";
    private const string BlockSelector = "h1, h2, h3, h4, h5, h6, p, li, blockquote, pre, figcaption, td, th";

    /// <inheritdoc />
    public bool CanParse(string contentType) =>
        SupportedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<SourceDocument> ParseAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);

        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(content, cancellationToken).ConfigureAwait(false);

        foreach (var noise in document.QuerySelectorAll(NoiseSelector).ToList())
        {
            noise.Remove();
        }

        // Only outermost matches become paragraphs, so a <li> containing a <p> does
        // not contribute its text twice.
        var matched = document.QuerySelectorAll(BlockSelector).ToList();
        var matchedSet = new HashSet<IElement>(matched);
        var paragraphs = new List<string>();
        foreach (var element in matched)
        {
            if (HasMatchedAncestor(element, matchedSet))
            {
                continue;
            }

            var text = NormalizeWhitespace(element.TextContent);
            if (text.Length > 0)
            {
                paragraphs.Add(text);
            }
        }

        // Markup with no block structure at all still yields its flattened text.
        if (paragraphs.Count == 0 && document.Body is { } body)
        {
            var text = NormalizeWhitespace(body.TextContent);
            if (text.Length > 0)
            {
                paragraphs.Add(text);
            }
        }

        var metadata = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(document.Title))
        {
            metadata["title"] = document.Title.Trim();
        }

        return new SourceDocument
        {
            Id = Guid.CreateVersion7().ToString("n"),
            Text = string.Join("\n\n", paragraphs),
            Metadata = metadata,
        };
    }

    private static bool HasMatchedAncestor(IElement element, HashSet<IElement> matched)
    {
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (matched.Contains(parent))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
