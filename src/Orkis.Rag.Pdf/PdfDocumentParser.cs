using System.Globalization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Orkis.Retrieval;

/// <summary>
/// Parses PDF content into a <see cref="SourceDocument"/> using PdfPig, extracting
/// text in content order with pages separated by blank lines. Scanned PDFs that carry
/// an OCR text layer extract through that layer like any other text; image-only PDFs
/// with no embedded text legitimately yield an empty document — OCR is a separate
/// capability, not this parser's job. PDFs with broken font-to-Unicode mappings can
/// extract as garbled text; that is a defect of the producing tool.
/// </summary>
public sealed class PdfDocumentParser : IDocumentParser
{
    /// <inheritdoc />
    public bool CanParse(string contentType) =>
        string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<SourceDocument> ParseAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);

        // PdfPig needs random access; the source stream may not be seekable.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var pages = new List<string>();
        var metadata = new Dictionary<string, string>();
        using (var document = PdfDocument.Open(buffer.ToArray()))
        {
            metadata["pages"] = document.NumberOfPages.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(document.Information.Title))
            {
                metadata["title"] = document.Information.Title.Trim();
            }

            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = ContentOrderTextExtractor.GetText(page);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    pages.Add(text.Trim());
                }
            }
        }

        return new SourceDocument
        {
            Id = Guid.CreateVersion7().ToString("n"),
            Text = string.Join("\n\n", pages),
            Metadata = metadata,
        };
    }
}
