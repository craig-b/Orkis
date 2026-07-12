using System.Globalization;
using System.Text;
using System.Text.Json;
using Orkis.Tools;

namespace Orkis.Retrieval;

/// <summary>
/// Exposes corpus retrieval to the agent as a <c>search_corpus</c> tool: the model
/// decides when to look things up, consistent with how it discovers tools. When an
/// <see cref="IReranker"/> is available, a wider first-stage retrieval is reranked
/// down to the requested count. Results carry chunk ids and source metadata so
/// answers can cite what they used.
/// </summary>
public sealed class RetrievalTool(IRetriever retriever, IReranker? reranker = null) : ITool
{
    /// <summary>First-stage candidate count when a reranker narrows the results.</summary>
    private const int RerankCandidates = 20;

    public ToolDescriptor Descriptor { get; } =
        new()
        {
            Name = "search_corpus",
            Description =
                "Searches the indexed document corpus and returns the most relevant passages, "
                + "with their ids and source metadata. Quote or cite only what it returns.",
            ParametersSchema = JsonDocument
                .Parse(
                    """
                    {"type":"object","properties":{"query":{"type":"string","description":"What to search for."},"top_k":{"type":"integer","description":"Maximum passages to return (default 5)."}},"required":["query"]}
                    """
                )
                .RootElement,
            Risk = ToolRisk.ReadOnly,
        };

    public async Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var query = toolCall.Arguments.TryGetProperty("query", out var queryProperty)
            ? queryProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult
            {
                ToolCallId = toolCall.Id,
                Content = "Missing required argument 'query'.",
                IsError = true,
            };
        }

        var topK =
            toolCall.Arguments.TryGetProperty("top_k", out var topKProperty)
            && topKProperty.TryGetInt32(out var requested)
            && requested > 0
                ? requested
                : 5;

        var results = await retriever
            .RetrieveAsync(
                new RetrievalQuery { Text = query, TopK = reranker is null ? topK : RerankCandidates },
                cancellationToken
            )
            .ConfigureAwait(false);

        if (reranker is not null && results.Count > 0)
        {
            var reranked = await reranker.RerankAsync(query, results, cancellationToken).ConfigureAwait(false);
            results = [.. reranked.Take(topK)];
        }

        return new ToolResult { ToolCallId = toolCall.Id, Content = Render(results) };
    }

    private static string Render(IReadOnlyList<Scored<Chunk>> results)
    {
        if (results.Count == 0)
        {
            return "No matching content in the corpus. Try different terms, or say so if the answer isn't there.";
        }

        var builder = new StringBuilder();
        foreach (var (chunk, score) in results.Select(static r => (r.Item, r.Score)))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append('[').Append(chunk.Id).Append("] (score ");
            builder.Append(score.ToString("0.###", CultureInfo.InvariantCulture));
            foreach (var (key, value) in chunk.Metadata)
            {
                builder.Append(", ").Append(key).Append('=').Append(value);
            }

            builder.AppendLine(")");
            builder.AppendLine(chunk.Text);
        }

        return builder.ToString().TrimEnd();
    }
}
