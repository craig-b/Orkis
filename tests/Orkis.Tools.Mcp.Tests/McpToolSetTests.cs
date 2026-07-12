using System.Text.Json;
using Orkis.Tools;

namespace Orkis.Tools.Mcp.Tests;

// These tests launch a real (minimal) MCP server over stdio — fixtures/test-mcp-server.py —
// so the adapter is verified against an actual protocol conversation. They self-skip
// (pass vacuously) where python3 is unavailable.
public sealed class McpToolSetTests
{
    private static readonly string ServerScript = Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "test-mcp-server.py"
    );

    private static bool Available =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':')
            .Any(dir => File.Exists(Path.Combine(dir, "python3")));

    private static McpStdioServerOptions Options(bool trustAnnotations = false)
    {
        var options = new McpStdioServerOptions { Command = "python3", TrustAnnotations = trustAnnotations };
        options.Arguments.Add(ServerScript);
        return options;
    }

    private static ToolCall Call(string toolName, string argumentsJson) =>
        new()
        {
            Id = "call-1",
            ToolName = toolName,
            Arguments = JsonSerializer.Deserialize<JsonElement>(argumentsJson),
        };

    [Fact]
    public async Task ListsToolsWithSchemasAndUntrustedRiskDefault()
    {
        if (!Available)
        {
            return;
        }

        await using var toolSet = await McpToolSet.ConnectAsync(Options());

        Assert.Equal(["add", "explode"], toolSet.Tools.Select(t => t.Descriptor.Name).Order());
        var add = toolSet.Tools.Single(t => t.Descriptor.Name == "add");
        Assert.Equal("Adds two integers.", add.Descriptor.Description);
        Assert.Contains("\"a\"", add.Descriptor.ParametersSchema.GetRawText(), StringComparison.Ordinal);

        // Annotations are unverified server claims; without opt-in everything is Mutating.
        Assert.All(toolSet.Tools, tool => Assert.Equal(ToolRisk.Mutating, tool.Descriptor.Risk));
    }

    [Fact]
    public async Task TrustedAnnotationsMapOntoRisk()
    {
        if (!Available)
        {
            return;
        }

        await using var toolSet = await McpToolSet.ConnectAsync(Options(trustAnnotations: true));

        Assert.Equal(ToolRisk.ReadOnly, toolSet.Tools.Single(t => t.Descriptor.Name == "add").Descriptor.Risk);
        Assert.Equal(ToolRisk.Destructive, toolSet.Tools.Single(t => t.Descriptor.Name == "explode").Descriptor.Risk);
    }

    [Fact]
    public async Task InvocationRoundTripsThroughTheServer()
    {
        if (!Available)
        {
            return;
        }

        await using var toolSet = await McpToolSet.ConnectAsync(Options());
        var add = toolSet.Tools.Single(t => t.Descriptor.Name == "add");

        var result = await add.InvokeAsync(Call("add", """{"a":19,"b":23}"""));

        Assert.False(result.IsError);
        Assert.Equal("42", result.Content);
        Assert.Equal("call-1", result.ToolCallId);
    }

    [Fact]
    public async Task ServerErrorsMapToErrorResults()
    {
        if (!Available)
        {
            return;
        }

        await using var toolSet = await McpToolSet.ConnectAsync(Options());
        var explode = toolSet.Tools.Single(t => t.Descriptor.Name == "explode");

        var result = await explode.InvokeAsync(Call("explode", "{}"));

        Assert.True(result.IsError);
        Assert.Equal("kaboom", result.Content);
    }
}
