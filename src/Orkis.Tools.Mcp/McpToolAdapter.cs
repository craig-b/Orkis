using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Orkis.Tools;

/// <summary>
/// Presents one MCP server tool as an Orkis <see cref="ITool"/>: the server's schema
/// becomes the descriptor, invocations go over the MCP connection, and results map to
/// <see cref="ToolResult"/> (text blocks concatenated; the server's error flag
/// preserved). Risk defaults to <see cref="ToolRisk.Mutating"/> unless the host opted
/// into trusting the server's annotations.
/// </summary>
internal sealed class McpToolAdapter : ITool
{
    private readonly McpClient _client;
    private readonly McpClientTool _tool;

    public McpToolAdapter(McpClient client, McpClientTool tool, bool trustAnnotations)
    {
        _client = client;
        _tool = tool;
        Descriptor = new ToolDescriptor
        {
            Name = tool.Name,
            Description = tool.Description ?? "",
            ParametersSchema = tool.JsonSchema,
            Risk = MapRisk(tool, trustAnnotations),
        };
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in toolCall.Arguments.EnumerateObject())
        {
            arguments[property.Name] = property.Value;
        }

        var result = await _client
            .CallToolAsync(_tool.Name, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ToolResult
        {
            ToolCallId = toolCall.Id,
            Content = RenderContent(result),
            IsError = result.IsError == true,
        };
    }

    private static ToolRisk MapRisk(McpClientTool tool, bool trustAnnotations)
    {
        if (!trustAnnotations)
        {
            return ToolRisk.Mutating;
        }

        return tool.ProtocolTool.Annotations switch
        {
            { ReadOnlyHint: true } => ToolRisk.ReadOnly,
            { DestructiveHint: true } => ToolRisk.Destructive,
            _ => ToolRisk.Mutating,
        };
    }

    private static string RenderContent(CallToolResult result)
    {
        var builder = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(
                block switch
                {
                    TextContentBlock text => text.Text,
                    _ => $"[unsupported {block.Type} content]",
                }
            );
        }

        return builder.ToString();
    }
}
