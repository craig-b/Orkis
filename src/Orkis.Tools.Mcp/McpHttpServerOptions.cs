namespace Orkis.Tools;

/// <summary>
/// Configuration for connecting to a remote MCP server over Streamable HTTP.
/// Remember that every tool invocation ships its arguments to that server — treat
/// the endpoint with the same care as any third party receiving run data.
/// </summary>
public sealed class McpHttpServerOptions : McpServerOptionsBase
{
    /// <summary>The server's MCP endpoint.</summary>
    public required Uri Endpoint { get; set; }

    /// <summary>Additional request headers, e.g. an Authorization bearer token.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
}
