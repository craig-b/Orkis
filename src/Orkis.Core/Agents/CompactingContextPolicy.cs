using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Orkis.Agents;

/// <summary>
/// The default context policy. Under the trigger threshold the transcript passes
/// through untouched. Over it, compaction is tiered and chunky: first aged tool
/// outputs are stubbed (cheap, deterministic), then — when a summarizer client is
/// provided and the window is still over the trigger — everything between the first
/// user message and the recent tail is folded into one summary produced by a model
/// call. Summaries are cached by the transcript range they cover, so each range is
/// summarized once per run ever (the cache lives in run state and checkpoints), and
/// later compactions chain: a new summary consumes the previous one plus the raw
/// messages after it, keeping compaction infrequent and prompt caches mostly stable.
/// </summary>
public sealed class CompactingContextPolicy : IContextPolicy
{
    private const string SummaryCachePrefix = "summary:";

    private const string SummarizerInstructions =
        "Summarize this agent conversation span for the agent itself, which resumes "
        + "work with only your summary in place of the original messages. Preserve "
        + "decisions, established facts, file paths, identifiers, tool outcomes, and "
        + "open tasks. Note failures and dead ends so they are not repeated. Be concise.";

    private readonly IChatClient? _summarizer;
    private readonly CompactingContextPolicyOptions _options;

    public CompactingContextPolicy(
        IChatClient? summarizer = null,
        IOptions<CompactingContextPolicyOptions>? options = null
    )
    {
        _summarizer = summarizer;
        _options = options?.Value ?? new CompactingContextPolicyOptions();
    }

    /// <inheritdoc />
    public async Task<ContextView> ComposeAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transcript = request.Transcript;
        if (EstimateTokens(transcript, request) <= _options.TriggerTokens)
        {
            return new ContextView { Messages = transcript };
        }

        // Tier 1: stub aged tool outputs, keeping the recent tail verbatim.
        var protectedFrom = Math.Max(0, transcript.Count - _options.KeepRecentMessages);
        var stubbed = transcript
            .Select((message, index) => index < protectedFrom ? StubToolResults(message) : message)
            .ToList();

        if (
            _summarizer is null
            || EstimateTokens(stubbed, request) <= _options.TriggerTokens
            || !TryPlanSummarySpan(transcript, protectedFrom, out var spanStart, out var spanEnd)
        )
        {
            return new ContextView { Messages = stubbed };
        }

        // Tier 2: fold [spanStart..spanEnd] into one summary, reusing or chaining
        // cached summaries of earlier ranges.
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{SummaryCachePrefix}{spanStart}-{spanEnd}");
        var cacheUpdates = new Dictionary<string, string>();
        if (!request.Cache.TryGetValue(cacheKey, out var summary))
        {
            var (priorSummary, priorEnd) = FindLatestCachedSummary(request.Cache, spanStart, spanEnd);
            var rendered = RenderForSummarizer(stubbed, priorSummary, priorEnd + 1, spanStart, spanEnd);
            var response = await _summarizer
                .GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, SummarizerInstructions),
                        new ChatMessage(ChatRole.User, rendered),
                    ],
                    new ChatOptions { Temperature = 0 },
                    cancellationToken
                )
                .ConfigureAwait(false);
            summary = response.Text;
            cacheUpdates[cacheKey] = summary;
        }

        var composed = new List<ChatMessage>();
        composed.AddRange(stubbed.Take(spanStart));
        composed.Add(
            new ChatMessage(
                ChatRole.User,
                "[Summary of the earlier conversation, generated during context compaction. "
                    + "Details were elided; re-derive anything load-bearing with tools.]\n"
                    + summary
            )
        );
        composed.AddRange(stubbed.Skip(spanEnd + 1));

        return new ContextView { Messages = composed, CacheUpdates = cacheUpdates };
    }

    private static long EstimateTokens(IReadOnlyList<ChatMessage> messages, ContextRequest request) =>
        ContextEstimator.EstimateTokens(ContextEstimator.CountChars(messages), request.ObservedCharsPerToken);

    private ChatMessage StubToolResults(ChatMessage message)
    {
        if (message.Role != ChatRole.Tool)
        {
            return message;
        }

        var contents = new List<AIContent>(message.Contents.Count);
        var changed = false;
        foreach (var content in message.Contents)
        {
            if (
                content is FunctionResultContent result
                && result.Result?.ToString() is { } text
                && text.Length > _options.MaxAgedToolResultChars
            )
            {
                FunctionResultContent stub = new(
                    result.CallId,
                    text[.._options.MaxAgedToolResultChars]
                        + $"… [elided {text.Length - _options.MaxAgedToolResultChars} chars during context compaction]"
                );
                contents.Add(stub);
                changed = true;
            }
            else
            {
                contents.Add(content);
            }
        }

        return changed ? new ChatMessage(message.Role, contents) : message;
    }

    /// <summary>
    /// Plans the range to summarize: after the system prompt and first user message,
    /// up to the recent tail — shrunk so the cut never separates an assistant tool
    /// call from its tool results.
    /// </summary>
    private static bool TryPlanSummarySpan(
        IReadOnlyList<ChatMessage> transcript,
        int protectedFrom,
        out int start,
        out int end
    )
    {
        start = 0;
        while (start < transcript.Count && transcript[start].Role == ChatRole.System)
        {
            start++;
        }

        if (start < transcript.Count && transcript[start].Role == ChatRole.User)
        {
            start++; // The original task statement stays verbatim.
        }

        end = protectedFrom - 1;
        while (end >= start && end + 1 < transcript.Count && transcript[end + 1].Role == ChatRole.Tool)
        {
            end--; // Never orphan tool results from their call.
        }

        // Only worth a model call when the span covers several messages.
        return end - start >= 3;
    }

    private static (string? Summary, int End) FindLatestCachedSummary(
        IReadOnlyDictionary<string, string> cache,
        int spanStart,
        int spanEnd
    )
    {
        string? best = null;
        var bestEnd = spanStart - 1;
        foreach (var (key, value) in cache)
        {
            if (!key.StartsWith(SummaryCachePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = key[SummaryCachePrefix.Length..].Split('-');
            if (
                parts.Length == 2
                && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var cachedStart)
                && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var cachedEnd)
                && cachedStart == spanStart
                && cachedEnd < spanEnd
                && cachedEnd > bestEnd
            )
            {
                best = value;
                bestEnd = cachedEnd;
            }
        }

        return (best, bestEnd);
    }

    private static string RenderForSummarizer(
        List<ChatMessage> messages,
        string? priorSummary,
        int rawFrom,
        int spanStart,
        int spanEnd
    )
    {
        var builder = new StringBuilder();
        if (priorSummary is not null)
        {
            builder.AppendLine("[Summary of the span's earlier part:]").AppendLine(priorSummary).AppendLine();
            builder.AppendLine("[Messages after that summary:]");
        }

        for (var i = Math.Max(spanStart, rawFrom); i <= spanEnd; i++)
        {
            var message = messages[i];
            builder.Append(message.Role.Value).Append(": ");
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        builder.Append(text.Text);
                        break;
                    case FunctionCallContent call:
                        builder.Append("[called ").Append(call.Name).Append(']');
                        break;
                    case FunctionResultContent result:
                        builder.Append("[result] ").Append(result.Result);
                        break;
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}
