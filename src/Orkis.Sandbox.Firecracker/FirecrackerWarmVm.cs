using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Orkis.Sandboxing;

/// <summary>
/// A warm, reused Firecracker micro-VM serving commands for one workspace image via
/// the guest agent's vsock protocol. Boot cost is paid once per VM rather than per
/// command; concurrent commands are ordinary in-OS concurrency inside the one VM; an
/// idle timeout shuts the VM down, losing only in-memory state — the workspace image
/// survives and the next command boots a fresh VM over it.
/// </summary>
internal sealed class FirecrackerWarmVm : IAsyncDisposable
{
    private const int AgentPort = 52000;

    private readonly Process _process;
    private readonly string _vmDirectory;
    private readonly string _vsockPath;
    private readonly TimeSpan _idleTimeout;
    private readonly ITimer _idleTimer;
    private readonly Lock _stateLock = new();
    private int _inFlight;
    private bool _disposed;

    private FirecrackerWarmVm(
        Process process,
        string vmDirectory,
        string vsockPath,
        TimeSpan idleTimeout,
        Action<FirecrackerWarmVm> onIdle,
        TimeProvider timeProvider
    )
    {
        _process = process;
        _vmDirectory = vmDirectory;
        _vsockPath = vsockPath;
        _idleTimeout = idleTimeout;
        _idleTimer = timeProvider.CreateTimer(
            _ =>
            {
                bool fire;
                lock (_stateLock)
                {
                    fire = _inFlight == 0 && !_disposed;
                }

                if (fire)
                {
                    onIdle(this);
                }
            },
            state: null,
            idleTimeout,
            Timeout.InfiniteTimeSpan
        );
    }

    /// <summary>Commands currently executing in the VM.</summary>
    public int InFlight
    {
        get
        {
            lock (_stateLock)
            {
                return _inFlight;
            }
        }
    }

    /// <summary>
    /// Boots a VM from the given config and waits for the guest agent to accept a
    /// vsock connection. Returns <see langword="null"/> when the agent never comes up
    /// (e.g. a rootfs without the agent) — the caller falls back to cold execution.
    /// </summary>
    public static async Task<FirecrackerWarmVm?> TryBootAsync(
        FirecrackerSandboxOptions options,
        string vmConfigJson,
        string vmDirectory,
        Action<FirecrackerWarmVm> onIdle,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(vmDirectory);
        var vsockPath = Path.Combine(vmDirectory, "vsock.sock");
        var configPath = Path.Combine(vmDirectory, "vm.json");
        await File.WriteAllTextAsync(configPath, vmConfigJson, cancellationToken).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.FirecrackerPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--no-api");
        startInfo.ArgumentList.Add("--config-file");
        startInfo.ArgumentList.Add(configPath);

        var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();
        DrainInBackground(process.StandardOutput);
        DrainInBackground(process.StandardError);

        var start = timeProvider.GetTimestamp();
        while (timeProvider.GetElapsedTime(start) < options.AgentReadyTimeout)
        {
            if (process.HasExited)
            {
                break;
            }

            try
            {
                var probe = await ConnectToAgentAsync(vsockPath, cancellationToken).ConfigureAwait(false);
                await probe.DisposeAsync().ConfigureAwait(false);
                return new FirecrackerWarmVm(process, vmDirectory, vsockPath, options.WarmVmIdleTimeout, onIdle, timeProvider);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                // Not listening yet (or never will be); keep polling until the deadline.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken).ConfigureAwait(false);
        }

        TryKill(process);
        process.Dispose();
        TryDeleteDirectory(vmDirectory);
        return null;
    }

    /// <summary>Marks a command as starting, pausing the idle countdown. Pair with <see cref="EndCommand"/>.</summary>
    public void BeginCommand()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _inFlight++;
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Marks a command as finished; restarts the idle countdown when none remain.</summary>
    public void EndCommand()
    {
        lock (_stateLock)
        {
            _inFlight--;
            if (_inFlight == 0 && !_disposed)
            {
                _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Sends one agent request line and returns the response line. The deadline bounds
    /// the whole exchange; past it the VM is presumed hung and the caller disposes it.
    /// </summary>
    public async Task<JsonDocument> SendRequestAsync(
        string requestJsonLine,
        TimeSpan deadline,
        CancellationToken cancellationToken
    )
    {
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(deadline);

        var stream = await ConnectToAgentAsync(_vsockPath, deadlineSource.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var payload = Encoding.UTF8.GetBytes(requestJsonLine + "\n");
            await stream.WriteAsync(payload, deadlineSource.Token).ConfigureAwait(false);

            var line = await ReadResponseLineAsync(stream, deadlineSource.Token).ConfigureAwait(false);
            return JsonDocument.Parse(line);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _idleTimer.Dispose();

        // Ask the agent to sync, unmount, and halt, so the workspace journal is clean;
        // fall back to killing the VMM if the agent is unresponsive.
        try
        {
            using var shutdownSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendRequestAsync("""{"shutdown": true}""", TimeSpan.FromSeconds(5), shutdownSource.Token)
                .ConfigureAwait(false);
            await _process.WaitForExitAsync(shutdownSource.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or JsonException)
        {
            TryKill(_process);
        }

        _process.Dispose();
        TryDeleteDirectory(_vmDirectory);
    }

    private static async Task<NetworkStream> ConnectToAgentAsync(string vsockPath, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(vsockPath), cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);

            // Firecracker's host-side vsock protocol: "CONNECT <port>\n" -> "OK <n>\n",
            // then the stream is raw to the guest. The reply must be read byte-wise so
            // no agent response bytes are consumed with it.
            var connect = Encoding.ASCII.GetBytes($"CONNECT {AgentPort}\n");
            await stream.WriteAsync(connect, cancellationToken).ConfigureAwait(false);

            var reply = await ReadHandshakeLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!reply.StartsWith("OK ", StringComparison.Ordinal))
            {
                throw new IOException($"vsock handshake failed: '{reply}'.");
            }

            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<string> ReadHandshakeLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var line = new StringBuilder(16);
        while (line.Length < 64)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0 || buffer[0] == (byte)'\n')
            {
                break;
            }

            line.Append((char)buffer[0]);
        }

        return line.ToString();
    }

    private static async Task<string> ReadResponseLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        // Nothing follows the response line, so chunked reads past the newline are safe.
        var collected = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            collected.Write(buffer, 0, read);
            if (Array.IndexOf(buffer, (byte)'\n', 0, read) >= 0)
            {
                break;
            }
        }

        if (collected.Length == 0)
        {
            throw new IOException("The guest agent closed the connection without a response.");
        }

        return Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length);
    }

    private static void DrainInBackground(StreamReader reader) =>
        _ = Task.Run(async () =>
        {
            // The serial console keeps writing (boot noise, kernel messages); drain it
            // so the VMM never blocks on a full pipe. Content is discarded.
            var buffer = new char[4096];
            try
            {
                while (await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false) > 0) { }
            }
            catch (IOException)
            {
                // The process exited; nothing left to drain.
            }
        });

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
