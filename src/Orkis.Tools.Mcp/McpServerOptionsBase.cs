namespace Orkis.Tools;

/// <summary>Settings shared by every MCP server connection, regardless of transport.</summary>
public abstract class McpServerOptionsBase
{
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
