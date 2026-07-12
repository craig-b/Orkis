using System.Net;
using System.Net.Sockets;
using Orkis.Agents;
using Orkis.Client;

namespace Orkis.Daemon.Tests;

/// <summary>
/// Boots a daemon with an additional bearer-token TCP listener and verifies the auth
/// split: the Unix socket stays open (file permissions are its authentication) while
/// TCP requires the token on every request.
/// </summary>
public sealed class TcpAuthTests : IAsyncLifetime
{
    private const string Token = "test-token-1234";

    private Microsoft.AspNetCore.Builder.WebApplication? _app;
    private string? _root;
    private int _port;

    private string TcpEndpoint => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("orkis-tcp-tests-").FullName;
        _port = FreePort();
        _app = await DaemonApplication.CreateAsync(
            new DaemonSettings
            {
                SocketPath = Path.Combine(_root, "orkis.sock"),
                CheckpointDirectory = Path.Combine(_root, "checkpoints"),
                EventDirectory = Path.Combine(_root, "events"),
                ApprovalDirectory = Path.Combine(_root, "approvals"),
                ArtifactDirectory = Path.Combine(_root, "artifacts"),
                Offline = true,
                Sandbox = "process",
                ListenUrl = $"http://127.0.0.1:{_port}",
                BearerToken = Token,
            }
        );
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_root is not null)
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task TcpWithoutATokenIsUnauthorized()
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri($"{TcpEndpoint}/v1/runs"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TcpWithTheWrongTokenIsUnauthorized()
    {
        using var client = new OrkisClient(TcpEndpoint, "wrong-token");

        var ex = await Assert.ThrowsAsync<OrkisApiException>(() => client.ListRunsAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task TcpWithTheTokenDrivesARunEndToEnd()
    {
        using var client = new OrkisClient(TcpEndpoint, Token);

        var accepted = await client.StartRunAsync(
            new StartRunRequest { Prompt = "Run the greeting command.", SupervisorKey = "yolo" }
        );

        var stopAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < stopAt)
        {
            var run = await client.GetRunAsync(accepted.RunId);
            if (run is { Active: false, Status: RunStatus.Completed })
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The run did not complete over the TCP endpoint in time.");
    }

    [Fact]
    public async Task TheUnixSocketStaysOpenWithoutAToken()
    {
        using var client = new OrkisClient(Path.Combine(_root!, "orkis.sock"));

        Assert.True(await client.IsHealthyAsync());
    }
}
