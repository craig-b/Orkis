using System.Globalization;
using System.Text;
using System.Text.Json;
using Orkis.Tools;

namespace Orkis.Memory;

/// <summary>
/// Lets the agent search its memories mid-run. Results come from the run's scope plus
/// the global scope, and are framed as what they are: earlier model-written notes,
/// not verified facts.
/// </summary>
public sealed class SearchMemoriesTool(IMemoryStore store, string scope = MemoryScopes.Global) : ITool
{
    public ToolDescriptor Descriptor { get; } =
        new()
        {
            Name = "search_memories",
            Description =
                "Searches memories saved in earlier runs (this run's scope plus global). "
                + "Memories are unverified past notes — check against tools before relying on them.",
            ParametersSchema = JsonDocument
                .Parse(
                    """
                    {"type":"object","properties":{"query":{"type":"string","description":"What to recall."},"top_k":{"type":"integer","description":"Maximum memories to return (default 5)."}},"required":["query"]}
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

        var memories = await store.SearchAsync(query, scope, topK, cancellationToken).ConfigureAwait(false);
        if (memories.Count == 0)
        {
            return new ToolResult { ToolCallId = toolCall.Id, Content = "No relevant memories." };
        }

        var builder = new StringBuilder();
        foreach (var (entry, _) in memories.Select(static m => (m.Item, m.Score)))
        {
            builder
                .Append("- [")
                .Append(entry.Id)
                .Append("] (")
                .Append(entry.Scope)
                .Append(", ")
                .Append(entry.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append("): ")
                .AppendLine(entry.Text);
        }

        return new ToolResult { ToolCallId = toolCall.Id, Content = builder.ToString().TrimEnd() };
    }
}
