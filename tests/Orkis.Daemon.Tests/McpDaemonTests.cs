using Microsoft.Extensions.DependencyInjection;
using Orkis.Tools;

namespace Orkis.Daemon.Tests;

// Boots a daemon consuming a real (minimal) MCP server over stdio. Self-skips
// (passes vacuously) where python3 is unavailable.
public sealed class McpDaemonTests
{
    private static bool Available =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':')
            .Any(dir => File.Exists(Path.Combine(dir, "python3")));

    [Fact]
    public async Task DaemonStartsWithTheMcpServersToolsInTheCatalogue()
    {
        if (!Available)
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("orkis-daemon-mcp-").FullName;
        var serverScript = Path.Combine(AppContext.BaseDirectory, "fixtures", "test-mcp-server.py");
        try
        {
            var app = await DaemonApplication.CreateAsync(
                new DaemonSettings
                {
                    SocketPath = Path.Combine(root, "orkis.sock"),
                    CheckpointDirectory = Path.Combine(root, "checkpoints"),
                    EventDirectory = Path.Combine(root, "events"),
                    ApprovalDirectory = Path.Combine(root, "approvals"),
                    ArtifactDirectory = Path.Combine(root, "artifacts"),
                    Offline = true,
                    Sandbox = "process",
                    McpServers = [$"python3 {serverScript}"],
                }
            );
            await app.StartAsync();
            try
            {
                var toolSet = app.Services.GetRequiredService<McpToolSet>();
                Assert.NotEmpty(toolSet.Tools);

                // The tools live in the searchable catalogue, not the always-on set.
                var catalog = app.Services.GetRequiredService<IToolCatalog>();
                var matches = await catalog.SearchAsync(toolSet.Tools[0].Descriptor.Name);
                Assert.NotEmpty(matches);
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
