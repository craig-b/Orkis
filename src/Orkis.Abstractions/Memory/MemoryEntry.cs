using System.Collections.ObjectModel;

namespace Orkis.Memory;

/// <summary>
/// A single agent-written memory: a durable fact or observation recorded during a run,
/// distinct from corpus content indexed for retrieval.
/// </summary>
public sealed record MemoryEntry
{
    /// <summary>Stable identifier for this memory.</summary>
    public required string Id { get; init; }

    /// <summary>The remembered fact or observation.</summary>
    public required string Text { get; init; }

    /// <summary>When the memory was recorded.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Arbitrary metadata (source run, subject, category, …).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
