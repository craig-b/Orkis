using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Orkis.Agents;
using Orkis.Client;
using Orkis.Daemon;
using Orkis.Web;

namespace Orkis.Web.Tests;

/// <summary>
/// Boots a real daemon (socket-only) plus a gateway proxying to it, with auth forced
/// on so the token/session paths are exercised even over loopback.
/// </summary>
public sealed class GatewayFixture : IAsyncLifetime
{
    public const string Token = "gateway-test-token";

    private WebApplication? _daemon;
    private WebApplication? _gateway;
    private string? _root;

    public string GatewayUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("orkis-web-tests-").FullName;
        var socketPath = Path.Combine(_root, "orkis.sock");

        _daemon = await DaemonApplication.CreateAsync(
            new DaemonSettings
            {
                SocketPath = socketPath,
                CheckpointDirectory = Path.Combine(_root, "checkpoints"),
                EventDirectory = Path.Combine(_root, "events"),
                ApprovalDirectory = Path.Combine(_root, "approvals"),
                ArtifactDirectory = Path.Combine(_root, "artifacts"),
                Offline = true,
                Sandbox = "process",
            }
        );
        await _daemon.StartAsync();

        var port = FreePort();
        GatewayUrl = $"http://127.0.0.1:{port}";
        _gateway = GatewayApplication.Create(
            new WebSettings
            {
                ListenUrl = GatewayUrl,
                DaemonSocketPath = socketPath,
                BearerToken = Token,
                RequireAuthOnLoopback = true,
            }
        );
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_gateway is not null)
        {
            await _gateway.StopAsync();
            await _gateway.DisposeAsync();
        }

        if (_daemon is not null)
        {
            await _daemon.StopAsync();
            await _daemon.DisposeAsync();
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
}

public sealed class GatewayTests(GatewayFixture fixture) : IClassFixture<GatewayFixture>
{
    [Fact]
    public async Task RequestsWithoutCredentialsAreUnauthorized()
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri($"{fixture.GatewayUrl}/v1/healthz"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheUiShellIsReachableWithoutCredentials()
    {
        // The app shell must load unauthenticated so the browser can render the sign-in
        // form; only the API is gated. (Auth is forced on in the fixture, so this proves it
        // is the path, not loopback, that exempts it.)
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri($"{fixture.GatewayUrl}/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheWrongTokenCannotOpenASession()
    {
        using var http = new HttpClient();
        using var response = await http.PostAsJsonAsync(
            new Uri($"{fixture.GatewayUrl}/auth/session"),
            new { token = "wrong" }
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ASessionCookieAuthenticatesLikeTheToken()
    {
        using var handler = new HttpClientHandler { UseCookies = true };
        using var http = new HttpClient(handler);

        using var login = await http.PostAsJsonAsync(
            new Uri($"{fixture.GatewayUrl}/auth/session"),
            new { token = GatewayFixture.Token }
        );
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        using var proxied = await http.GetAsync(new Uri($"{fixture.GatewayUrl}/v1/healthz"));
        Assert.Equal(HttpStatusCode.OK, proxied.StatusCode);
    }

    [Fact]
    public async Task TheTypedClientDrivesARunThroughTheGateway()
    {
        // The same OrkisClient the CLI uses, pointed at the gateway URL: the whole
        // protocol — start, poll, SSE — crosses TCP, auth, and the socket proxy.
        using var client = new OrkisClient(fixture.GatewayUrl, GatewayFixture.Token);

        var accepted = await client.StartRunAsync(
            new StartRunRequest { Prompt = "Run the greeting command.", SupervisorKey = "yolo" }
        );

        var stopAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        RunResponse? run = null;
        while (DateTime.UtcNow < stopAt)
        {
            run = await client.GetRunAsync(accepted.RunId);
            if (run is { Active: false, Status: RunStatus.Completed })
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Equal(RunStatus.Completed, run?.Status);

        var sawCompletion = false;
        await foreach (var runEvent in client.StreamEventsAsync(accepted.RunId))
        {
            if (runEvent is Orkis.Runs.RunCompletedEvent)
            {
                sawCompletion = true;
            }
        }

        Assert.True(sawCompletion, "SSE did not stream through the gateway.");
    }

    [Fact]
    public async Task ThePlaceholderPageServesWhenAssetsAreAbsent()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", GatewayFixture.Token);
        var body = await http.GetStringAsync(new Uri($"{fixture.GatewayUrl}/"));

        Assert.Contains("Orkis", body);
    }
}
