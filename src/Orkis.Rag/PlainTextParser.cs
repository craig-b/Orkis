namespace Orkis.Retrieval;

/// <summary>Parses plain text and Markdown content into a <see cref="SourceDocument"/>.</summary>
public sealed class PlainTextParser : IDocumentParser
{
    private static readonly string[] SupportedContentTypes = ["text/plain", "text/markdown"];

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

        using var reader = new StreamReader(content, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return new SourceDocument { Id = Guid.CreateVersion7().ToString("n"), Text = text };
    }
}
