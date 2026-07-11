namespace Orkis.Runs;

/// <summary>
/// Price table for <see cref="PriceTableCostCalculator"/>. Prices change frequently,
/// so bind this from configuration rather than hardcoding.
/// </summary>
public sealed class CostOptions
{
    /// <summary>Prices keyed by model id (case-insensitive).</summary>
    public IDictionary<string, ModelPrice> Models { get; } =
        new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Price applied when a model has no entry in <see cref="Models"/>, or
    /// <see langword="null"/> to cost unknown models at zero.
    /// </summary>
    public ModelPrice? Fallback { get; set; }
}

/// <summary>Per-model prices, expressed per million tokens.</summary>
public sealed class ModelPrice
{
    /// <summary>Price per million input tokens.</summary>
    public decimal InputPerMillionTokens { get; set; }

    /// <summary>Price per million output tokens.</summary>
    public decimal OutputPerMillionTokens { get; set; }

    /// <summary>
    /// Prices for provider-specific buckets, keyed by the provider's count name
    /// (e.g. "cache_read_input_tokens", "cache_creation_input_tokens"). Buckets
    /// without a configured price cost zero.
    /// </summary>
    public IDictionary<string, decimal> AdditionalPerMillionTokens { get; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
}
