namespace Orkis.Tools;

/// <summary>Configuration for connecting to one MCP server over stdio.</summary>
public sealed class McpStdioServerOptions
{
    /// <summary>The server executable to launch.</summary>
    public required string Command { get; set; }

    /// <summary>Arguments passed to the server executable.</summary>
    public IList<string> Arguments { get; } = [];

    /// <summary>Environment variables for the server process (API keys and the like).</summary>
    public IDictionary<string, string?> EnvironmentVariables { get; } = new Dictionary<string, string?>();

    /// <summary>Display name used in logs and errors.</summary>
    public string Name { get; set; } = "mcp";

    /// <summary>
    /// Whether to map the server's self-declared tool annotations (read-only,
    /// destructive) onto <see cref="ToolRisk"/>. Off by default: risk classification
    /// gates supervision, and annotations are unverified claims by the server — a tool
    /// wrongly marked read-only would skip review under threshold policies. Enable
    /// only for servers you trust as much as your own code; otherwise every MCP tool
    /// is treated as <see cref="ToolRisk.Mutating"/> and goes through supervision.
    /// </summary>
    public bool TrustAnnotations { get; set; }
}
