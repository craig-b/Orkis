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
                var registry = app.Services.GetRequiredService<McpServerRegistry>();
                var servers = registry.List();
                Assert.Single(servers);
                Assert.NotEmpty(servers[0].Tools);

                // The tools live in the searchable catalogue, not the always-on set.
                var catalog = app.Services.GetRequiredService<IToolCatalog>();
                var matches = await catalog.SearchAsync(servers[0].Tools[0]);
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

    [Fact]
    public async Task ServersAddedAndRemovedAtRuntimeUpdateTheCatalogue()
    {
        if (!Available)
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("orkis-daemon-mcp-runtime-").FullName;
        var serverScript = Path.Combine(AppContext.BaseDirectory, "fixtures", "test-mcp-server.py");
        try
        {
            // Boot with no MCP servers: the catalogue starts empty.
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
                }
            );
            await app.StartAsync();
            try
            {
                var registry = app.Services.GetRequiredService<McpServerRegistry>();
                var catalog = app.Services.GetRequiredService<IToolCatalog>();
                Assert.Empty(registry.List());
                Assert.Equal(0, catalog.Count);

                // Connect at runtime: its tools become searchable.
                var added = await registry.AddAsync($"python3 {serverScript}");
                Assert.NotEmpty(added.Tools);
                Assert.Equal(catalog.Count, added.Tools.Count);
                Assert.NotEmpty(await catalog.SearchAsync(added.Tools[0]));

                // A second connection of the same server gets a distinct name.
                var second = await registry.AddAsync($"python3 {serverScript}");
                Assert.NotEqual(added.Name, second.Name);
                Assert.Equal(2, registry.List().Count);

                // Disconnect: the first server's tools leave the catalogue.
                Assert.True(await registry.RemoveAsync(added.Name));
                Assert.False(await registry.RemoveAsync(added.Name));
                Assert.Single(registry.List());
                Assert.Equal(second.Tools.Count, catalog.Count);
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
