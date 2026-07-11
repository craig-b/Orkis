using Microsoft.Extensions.Options;

namespace Orkis.Retrieval;

/// <summary>
/// Paragraph-aware chunker: packs whole paragraphs into chunks up to a maximum
/// length, hard-splitting (with overlap) only when a single paragraph exceeds it.
/// </summary>
public sealed class TextChunker : IChunker
{
    private readonly TextChunkerOptions _options;

    public TextChunker(IOptions<TextChunkerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        if (_options.MaxChunkLength < 1)
        {
            throw new ArgumentException("MaxChunkLength must be positive.", nameof(options));
        }

        if (_options.HardSplitOverlap < 0 || _options.HardSplitOverlap >= _options.MaxChunkLength)
        {
            throw new ArgumentException(
                "HardSplitOverlap must be non-negative and smaller than MaxChunkLength.",
                nameof(options)
            );
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Chunk>> ChunkAsync(SourceDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var pieces = new List<string>();
        var buffer = new System.Text.StringBuilder();

        foreach (var paragraph in Paragraphs(document.Text))
        {
            if (paragraph.Length > _options.MaxChunkLength)
            {
                Flush(pieces, buffer);
                HardSplit(pieces, paragraph);
            }
            else if (buffer.Length > 0 && buffer.Length + 2 + paragraph.Length > _options.MaxChunkLength)
            {
                Flush(pieces, buffer);
                buffer.Append(paragraph);
            }
            else
            {
                if (buffer.Length > 0)
                {
                    buffer.Append("\n\n");
                }

                buffer.Append(paragraph);
            }
        }

        Flush(pieces, buffer);

        IReadOnlyList<Chunk> chunks =
        [
            .. pieces.Select(
                (text, index) =>
                    new Chunk
                    {
                        Id = $"{document.Id}:{index:D4}",
                        DocumentId = document.Id,
                        Text = text,
                        Metadata = document.Metadata,
                    }
            ),
        ];
        return Task.FromResult(chunks);
    }

    private static IEnumerable<string> Paragraphs(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static paragraph => paragraph.Length > 0);

    private static void Flush(List<string> pieces, System.Text.StringBuilder buffer)
    {
        if (buffer.Length > 0)
        {
            pieces.Add(buffer.ToString());
            buffer.Clear();
        }
    }

    private void HardSplit(List<string> pieces, string paragraph)
    {
        var step = _options.MaxChunkLength - _options.HardSplitOverlap;
        for (var start = 0; start < paragraph.Length; start += step)
        {
            var length = Math.Min(_options.MaxChunkLength, paragraph.Length - start);
            pieces.Add(paragraph.Substring(start, length));
            if (start + _options.MaxChunkLength >= paragraph.Length)
            {
                break;
            }
        }
    }
}
