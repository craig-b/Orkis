using Orkis.Sandboxing;
using Orkis.Tools;

namespace Orkis.Host;

/// <summary>
/// Wraps a tool and prints each invocation and its raw result to the console, so the
/// demo shows exactly what tools returned rather than only the model's summary of them.
/// Forwards <see cref="ISandboxedTool"/> so sandbox-level requirements still reach the inner tool.
/// </summary>
public sealed class ConsoleLoggingTool(ITool inner) : ISandboxedTool
{
    public ToolDescriptor Descriptor => inner.Descriptor;

    public Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default) =>
        LogAsync(toolCall, () => inner.InvokeAsync(toolCall, cancellationToken), sandboxLevel: null);

    public Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        SandboxLevel minimumLevel,
        CancellationToken cancellationToken = default
    ) =>
        LogAsync(
            toolCall,
            () =>
                inner is ISandboxedTool sandboxed
                    ? sandboxed.InvokeAsync(toolCall, minimumLevel, cancellationToken)
                    : inner.InvokeAsync(toolCall, cancellationToken),
            minimumLevel
        );

    private static async Task<ToolResult> LogAsync(
        ToolCall toolCall,
        Func<Task<ToolResult>> invoke,
        SandboxLevel? sandboxLevel
    )
    {
        var sandboxNote = sandboxLevel is { } level ? $" [supervisor requires ≥ {level}]" : "";
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
