namespace Orkis.Retrieval;

/// <summary>Configuration for <see cref="TextChunker"/>.</summary>
public sealed class TextChunkerOptions
{
    /// <summary>Maximum chunk length in characters.</summary>
    public int MaxChunkLength { get; set; } = 2000;

    /// <summary>
    /// Characters of overlap between adjacent chunks when a single paragraph exceeds
    /// <see cref="MaxChunkLength"/> and must be hard-split.
    /// </summary>
    public int HardSplitOverlap { get; set; } = 200;
}
