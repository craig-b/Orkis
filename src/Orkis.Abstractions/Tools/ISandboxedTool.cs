using Orkis.Sandboxing;

namespace Orkis.Tools;

/// <summary>
/// A tool that can execute under caller-specified capabilities — isolation strength
/// and network reach. When a supervision decision carries an
/// <see cref="ExecutionGrant"/>, the agent runner uses this overload; tools that only
/// implement <see cref="ITool"/> cannot satisfy such a decision and the call is
/// rejected.
/// </summary>
public interface ISandboxedTool : ITool
{
    /// <summary>Executes the call under the capabilities of <paramref name="grant"/>.</summary>
    Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        ExecutionGrant grant,
        CancellationToken cancellationToken = default
    );
}
