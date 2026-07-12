namespace Orkis.Tools;

/// <summary>Configuration for connecting to one MCP server launched locally over stdio.</summary>
public sealed class McpStdioServerOptions : McpServerOptionsBase
{
    /// <summary>The server executable to launch.</summary>
    public required string Command { get; set; }

    /// <summary>Arguments passed to the server executable.</summary>
    public IList<string> Arguments { get; } = [];

    /// <summary>Environment variables for the server process (API keys and the like).</summary>
    public IDictionary<string, string?> EnvironmentVariables { get; } = new Dictionary<string, string?>();
}
