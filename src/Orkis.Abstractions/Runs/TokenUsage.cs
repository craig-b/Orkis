using System.Collections.ObjectModel;

namespace Orkis.Runs;

/// <summary>Token consumption of a single model call, in provider-reported buckets.</summary>
public sealed record TokenUsage
{
    /// <summary>Input tokens as reported by the provider.</summary>
    public long InputTokens { get; init; }

    /// <summary>Output tokens as reported by the provider.</summary>
    public long OutputTokens { get; init; }

    /// <summary>
    /// Provider-specific buckets beyond plain input/output — cache reads, cache writes,
    /// reasoning tokens — keyed by the provider's own count names.
    /// </summary>
    public IReadOnlyDictionary<string, long> AdditionalCounts { get; init; } = ReadOnlyDictionary<string, long>.Empty;

    /// <summary>The model that produced this usage, when known.</summary>
    public string? ModelId { get; init; }
}
