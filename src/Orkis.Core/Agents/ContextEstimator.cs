using Microsoft.Extensions.AI;

namespace Orkis.Agents;

/// <summary>
/// Character counting shared by the runner's calibration and context policies, so
/// both sides of the chars-per-token estimate measure the same thing.
/// </summary>
internal static class ContextEstimator
{
    public static long CountChars(IEnumerable<ChatMessage> messages)
    {
        long total = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                total += content switch
                {
                    TextContent text => text.Text.Length,
                    FunctionCallContent call => call.Name.Length + (call.Arguments?.Count ?? 0) * 16,
                    FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
                    _ => 0,
                };
            }
        }

        return total;
    }

    public static long EstimateTokens(long chars, double? observedCharsPerToken) =>
        (long)(chars / Math.Max(1.0, observedCharsPerToken ?? 4.0));
}
