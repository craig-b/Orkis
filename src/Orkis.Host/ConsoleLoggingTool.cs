using Orkis.Sandboxing;
using Orkis.Tools;

namespace Orkis.Host;

/// <summary>
/// Wraps a tool and prints each invocation and its raw result to the console, so the
/// demo shows exactly what tools returned rather than only the model's summary of them.
/// Forwards <see cref="ISandboxedTool"/> so execution grants still reach the inner tool.
/// </summary>
public sealed class ConsoleLoggingTool(ITool inner) : ISandboxedTool
{
    public ToolDescriptor Descriptor => inner.Descriptor;

    public Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default) =>
        LogAsync(toolCall, () => inner.InvokeAsync(toolCall, cancellationToken), grant: null);

    public Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        ExecutionGrant grant,
        CancellationToken cancellationToken = default
    ) =>
        LogAsync(
            toolCall,
            () =>
                inner is ISandboxedTool sandboxed
                    ? sandboxed.InvokeAsync(toolCall, grant, cancellationToken)
                    : inner.InvokeAsync(toolCall, cancellationToken),
            grant
        );

    private static async Task<ToolResult> LogAsync(
        ToolCall toolCall,
        Func<Task<ToolResult>> invoke,
        ExecutionGrant? grant
    )
    {
        var notes = new List<string>();
        if (grant?.MinimumSandboxLevel is { } level)
        {
            notes.Add($"≥ {level}");
        }

        if (grant?.Network is { } network)
        {
            notes.Add($"network: {network}");
        }

        var sandboxNote = notes.Count > 0 ? $" [supervisor grants {string.Join(", ", notes)}]" : "";
        Console.WriteLine($"→ tool '{toolCall.ToolName}'{sandboxNote} {toolCall.Arguments.GetRawText()}");

        var result = await invoke().ConfigureAwait(false);

        Console.WriteLine($"← {(result.IsError ? "error" : "ok")}:");
        foreach (var line in result.Content.Split('\n'))
        {
            Console.WriteLine($"    {line}");
        }

        return result;
    }
}
