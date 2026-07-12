using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;

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

    public static JsonSerializerOptions JsonOptions { get; } =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public async Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("orkis-daemon-tests-").FullName;
        var socketPath = Path.Combine(_root, "orkis.sock");

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

        _app = DaemonApplication.Create(settings);
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
