namespace Orkis.Runs;

/// <summary>
/// Computes the monetary cost of a model call from its token usage. Implementations
/// range from a configured price table to a pass-through for gateways that report
/// cost directly. The currency is whatever the implementation is configured in.
/// </summary>
public interface ICostCalculator
{
    /// <summary>Returns the cost of the call, or zero when no pricing is known.</summary>
    decimal Calculate(TokenUsage usage);
}
