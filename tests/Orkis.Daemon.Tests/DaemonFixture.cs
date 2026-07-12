using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Orkis.Daemon.Tests;

/// <summary>
/// Boots a real daemon — offline scripted model, process sandbox, durable stores in a
/// temp directory — listening on a Unix domain socket, and provides an
/// <see cref="HttpClient"/> connected to it. Everything the tests exercise crosses the
/// actual wire.
/// </summary>
public sealed class DaemonFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _root;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The daemon's socket, for tests that bring their own client.</summary>
    public string SocketPath { get; private set; } = null!;

    /// <summary>The daemon's services, for tests that seed state behind the API.</summary>
    public IServiceProvider Services => _app!.Services;

    public static JsonSerializerOptions JsonOptions { get; } =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public async Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("orkis-daemon-tests-").FullName;
        var socketPath = Path.Combine(_root, "orkis.sock");
        SocketPath = socketPath;

        var settings = new DaemonSettings
        {
            SocketPath = socketPath,
            CheckpointDirectory = Path.Combine(_root, "checkpoints"),
            EventDirectory = Path.Combine(_root, "events"),
            ApprovalDirectory = Path.Combine(_root, "approvals"),
            ArtifactDirectory = Path.Combine(_root, "artifacts"),
            WorkspaceKey = "tests",
            Offline = true,
            Sandbox = "process",
        };

        // An "alt" model key aliasing the default offline client, so model routing
        // is testable over the wire without a live provider.
        _app = await DaemonApplication.CreateAsync(
            settings,
            static services =>
                services.AddOrkisChatClient(
                    "alt",
                    static provider => provider.GetRequiredService<Microsoft.Extensions.AI.IChatClient>()
                )
        );
        await _app.StartAsync();

        Client = new HttpClient(
            new SocketsHttpHandler
            {
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            }
        )
        {
            BaseAddress = new Uri("http://daemon"),
        };
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
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
}
