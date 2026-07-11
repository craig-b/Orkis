using Microsoft.Extensions.Options;

namespace Orkis.Runs;

/// <summary>Computes cost from a configured price table, including cache and other provider-specific buckets.</summary>
public sealed class PriceTableCostCalculator : ICostCalculator
{
    private const decimal TokensPerPriceUnit = 1_000_000m;

    private readonly CostOptions _options;

    public PriceTableCostCalculator(IOptions<CostOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public decimal Calculate(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var price = ResolvePrice(usage.ModelId);
        if (price is null)
        {
            return 0m;
        }

        var cost =
            (usage.InputTokens * price.InputPerMillionTokens / TokensPerPriceUnit)
            + (usage.OutputTokens * price.OutputPerMillionTokens / TokensPerPriceUnit);

        foreach (var (bucket, count) in usage.AdditionalCounts)
        {
            if (price.AdditionalPerMillionTokens.TryGetValue(bucket, out var rate))
            {
                cost += count * rate / TokensPerPriceUnit;
            }
        }

        return cost;
    }

    /// <summary>
    /// Finds the price for a model id: exact match first, then the longest configured id
    /// that prefixes it at a '-' boundary — providers report dated snapshot ids
    /// (e.g. "gpt-5-mini-2025-08-07") that hosts configure by base id ("gpt-5-mini").
    /// </summary>
    private ModelPrice? ResolvePrice(string? modelId)
    {
        if (modelId is null)
        {
            return _options.Fallback;
        }

        if (_options.Models.TryGetValue(modelId, out var exact))
        {
            return exact;
        }

        ModelPrice? bestPrice = null;
        var bestLength = -1;
        foreach (var (configuredId, price) in _options.Models)
        {
            if (
                configuredId.Length > bestLength
                && modelId.Length > configuredId.Length
                && modelId[configuredId.Length] == '-'
                && modelId.StartsWith(configuredId, StringComparison.OrdinalIgnoreCase)
            )
            {
                bestPrice = price;
                bestLength = configuredId.Length;
            }
        }

        return bestPrice ?? _options.Fallback;
    }
}
