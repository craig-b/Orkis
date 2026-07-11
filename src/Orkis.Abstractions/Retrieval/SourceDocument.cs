using System.Collections.ObjectModel;

namespace Orkis.Retrieval;

/// <summary>A parsed source document, ready to be chunked and indexed.</summary>
public sealed record SourceDocument
{
    /// <summary>Stable identifier for the document.</summary>
    public required string Id { get; init; }

    /// <summary>The document's full text content.</summary>
    public required string Text { get; init; }

    /// <summary>Arbitrary metadata propagated to chunks produced from this document.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
