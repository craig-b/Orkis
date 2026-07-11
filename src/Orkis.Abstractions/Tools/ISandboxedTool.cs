using Orkis.Sandboxing;

namespace Orkis.Tools;

/// <summary>
/// A tool that can execute under a caller-specified isolation strength. When a
/// supervision decision requires a sandbox level, the agent runner uses this
/// overload; tools that only implement <see cref="ITool"/> cannot satisfy such
/// a decision and the call is rejected.
/// </summary>
public interface ISandboxedTool : ITool
{
    /// <summary>Executes the call under at least <paramref name="minimumLevel"/> isolation.</summary>
    Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        SandboxLevel minimumLevel,
        CancellationToken cancellationToken = default
    );
}
