namespace Orkis.Retrieval;

/// <summary>Parses raw content (PDF, HTML, Markdown, …) into a <see cref="SourceDocument"/>.</summary>
public interface IDocumentParser
{
    /// <summary>Returns whether this parser can handle content of the given MIME type.</summary>
    bool CanParse(string contentType);

    /// <summary>Parses the content stream into a document.</summary>
    Task<SourceDocument> ParseAsync(Stream content, string contentType, CancellationToken cancellationToken = default);
}
