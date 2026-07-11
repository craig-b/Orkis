using System.Collections.ObjectModel;

namespace Orkis.Retrieval;

/// <summary>
/// A unit of indexed content: a piece of a source document sized for embedding and retrieval.
/// </summary>
public sealed record Chunk
{
    /// <summary>Stable identifier for this chunk, unique within its store.</summary>
    public required string Id { get; init; }

    /// <summary>Identifier of the source document this chunk was taken from, if any.</summary>
    public string? DocumentId { get; init; }

    /// <summary>The chunk's text content.</summary>
    public required string Text { get; init; }

    /// <summary>Arbitrary metadata carried alongside the chunk (source, page, section, …).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
