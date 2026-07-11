namespace Orkis.Runs;

/// <summary>
/// Costs everything at zero. The default when no pricing is configured; note that
/// <see cref="RunBudget.MaxCost"/> cannot take effect under this calculator.
/// </summary>
public sealed class NullCostCalculator : ICostCalculator
{
    /// <summary>A shared instance.</summary>
    public static NullCostCalculator Instance { get; } = new();

    /// <inheritdoc />
    public decimal Calculate(TokenUsage usage) => 0m;
}
