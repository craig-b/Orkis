using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Orkis.Tools;

namespace Orkis.Tools.Mcp.Tests;

// Launches a real (minimal) MCP Streamable HTTP server — fixtures/test-mcp-http-server.py —
// and connects over the network, verifying the remote-transport path end to end.
// Self-skips (passes vacuously) where python3 is unavailable.
public sealed class McpHttpToolSetTests : IAsyncLifetime
{
    private static readonly string ServerScript = Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "test-mcp-http-server.py"
    );

    private static bool Available =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':')
            .Any(dir => File.Exists(Path.Combine(dir, "python3")));

    private Process? _server;
    private int _port;

    public async Task InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        _port = FreePort();
        _server = Process.Start(
            new ProcessStartInfo
            {
                FileName = "python3",
                ArgumentList = { ServerScript, _port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                UseShellExecute = false,
            }
        );

        // Wait for the listener to come up.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync("127.0.0.1", _port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException("The fixture HTTP server never started listening.");
    }

    public Task DisposeAsync()
    {
        if (_server is { } server)
        {
            try
            {
                server.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            server.Dispose();
        }

        return Task.CompletedTask;
    }

    private McpHttpServerOptions Options() =>
        new() { Endpoint = new Uri($"http://127.0.0.1:{_port}/mcp"), Name = "test-http" };

    [Fact]
    public async Task ConnectsListsAndInvokesOverHttp()
    {
        if (!Available)
        {
            return;
        }

        await using var toolSet = await McpToolSet.ConnectAsync(Options());

        var multiply = Assert.Single(toolSet.Tools);
        Assert.Equal("multiply", multiply.Descriptor.Name);
        Assert.Equal(ToolRisk.Mutating, multiply.Descriptor.Risk); // Annotations untrusted by default.

        var result = await multiply.InvokeAsync(
            new ToolCall
            {
                Id = "call-1",
                ToolName = "multiply",
                Arguments = JsonSerializer.Deserialize<JsonElement>("""{"a":6,"b":7}"""),
            }
        );

        Assert.False(result.IsError);
        Assert.Equal("42", result.Content);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
