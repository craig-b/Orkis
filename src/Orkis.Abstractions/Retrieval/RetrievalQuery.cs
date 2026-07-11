using System.Collections.ObjectModel;

namespace Orkis.Retrieval;

/// <summary>A request to retrieve chunks relevant to a piece of text.</summary>
public sealed record RetrievalQuery
{
    /// <summary>The text to find relevant chunks for.</summary>
    public required string Text { get; init; }

    /// <summary>Maximum number of chunks to return.</summary>
    public int TopK { get; init; } = 8;

    /// <summary>Metadata filters a chunk must match to be returned.</summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
