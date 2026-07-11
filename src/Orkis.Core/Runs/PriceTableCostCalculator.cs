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

        var price =
            usage.ModelId is { } modelId && _options.Models.TryGetValue(modelId, out var configured)
                ? configured
                : _options.Fallback;
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
}
