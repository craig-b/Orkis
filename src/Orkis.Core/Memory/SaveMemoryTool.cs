using System.Text.Json;
using Orkis.Tools;

namespace Orkis.Memory;

/// <summary>
/// Lets the agent record a durable memory. Declared mutating, so supervision reviews
/// what the agent wants to remember — and a wrong scope is corrected by denying with
/// a reason, prompting the model to re-save where it meant. The scope defaults to the
/// one this tool was constructed with (the run's scope); an explicit argument
/// overrides it, e.g. to promote a workload observation into global memory.
/// </summary>
public sealed class SaveMemoryTool(
    IMemoryStore store,
    string scope = MemoryScopes.Global,
    TimeProvider? timeProvider = null
) : ITool
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ToolDescriptor Descriptor { get; } =
        new()
        {
            Name = "save_memory",
            Description =
                "Saves a durable memory available to future runs. Use for stable facts and "
                + "preferences worth remembering, not transient run state. Scope defaults to "
                + $"this run's scope; \"{MemoryScopes.Global}\" makes it visible to every run.",
            ParametersSchema = JsonDocument
                .Parse(
                    """
                    {"type":"object","properties":{"text":{"type":"string","description":"The fact to remember, self-contained and concise."},"scope":{"type":"string","description":"Memory scope; omit for this run's scope."}},"required":["text"]}
                    """
                )
                .RootElement,
            Risk = ToolRisk.Mutating,
        };

    public async Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var text = toolCall.Arguments.TryGetProperty("text", out var textProperty) ? textProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ToolResult
            {
                ToolCallId = toolCall.Id,
                Content = "Missing required argument 'text'.",
                IsError = true,
            };
        }

        var requestedScope = toolCall.Arguments.TryGetProperty("scope", out var scopeProperty)
            ? scopeProperty.GetString()
            : null;

        var entry = new MemoryEntry
        {
            Id = Guid.CreateVersion7().ToString("n"),
            Text = text,
            Scope = string.IsNullOrWhiteSpace(requestedScope) ? scope : requestedScope,
            CreatedAt = _timeProvider.GetUtcNow(),
        };
        await store.WriteAsync(entry, cancellationToken).ConfigureAwait(false);

        return new ToolResult
        {
            ToolCallId = toolCall.Id,
            Content = $"Remembered in scope '{entry.Scope}' (id {entry.Id}).",
        };
    }
}
