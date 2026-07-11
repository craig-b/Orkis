using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Orkis.Sandboxing;

/// <summary>
/// Executes each command in its own Firecracker micro-VM: a hardware-virtualized
/// boundary (KVM) with a read-only root filesystem, a private writable /work drive,
/// and no network device. The VM boots, runs the command, and is destroyed. With a
/// <see cref="SandboxExecutionRequest.WorkspaceKey"/>, /work is a persistent per-key
/// image reused across executions, so files survive between commands; without one it
/// is a throwaway scratch.
/// </summary>
/// <remarks>
/// <para>
/// This provides <see cref="SandboxLevel.Strict"/> isolation — stronger than any
/// namespace-based sandbox, at the cost of ~0.5–2s per execution for boot and image
/// preparation. Requires KVM (<c>/dev/kvm</c>), the firecracker binary, a guest
/// kernel, and a rootfs honoring the Orkis /init contract; run
/// <c>scripts/setup-firecracker.sh</c> to provision the images.
/// </para>
/// <para>
/// Command output travels over the serial console between markers emitted by the
/// guest's /init, so stdout and stderr are captured separately and the guest exit
/// code is preserved. Production deployments should additionally run firecracker
/// under its jailer; this implementation launches it directly.
/// </para>
/// </remarks>
public sealed class FirecrackerSandbox : ISandbox
{
    private const string StdoutMarker = "===ORKIS:STDOUT===";
    private const string StderrMarker = "===ORKIS:STDERR===";
    private const string ExitMarker = "===ORKIS:EXIT:";

    // A rw ext4 image attached to two VMs at once corrupts; executions sharing a
    // persistent workspace serialize on a per-image gate. In-VM concurrency (one
    // machine, many processes) is the model's business; two machines on one disk
    // is not survivable and is ours.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WorkspaceGates = new();

    private readonly FirecrackerSandboxOptions _options;
    private readonly TimeProvider _timeProvider;

    public FirecrackerSandbox(IOptions<FirecrackerSandboxOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (string.IsNullOrEmpty(_options.KernelImagePath) || string.IsNullOrEmpty(_options.RootfsImagePath))
        {
            throw new ArgumentException(
                "KernelImagePath and RootfsImagePath must be configured; run scripts/setup-firecracker.sh.",
                nameof(options)
            );
        }

        if (_options.Network.Mode != NetworkMode.None)
        {
            throw new NotSupportedException(
                $"Network mode {_options.Network.Mode} is not yet implemented; only NetworkMode.None is supported. "
                    + "The micro-VM is configured with no network device."
            );
        }
    }

    /// <inheritdoc />
    public SandboxLevel Level => SandboxLevel.Strict;

    /// <inheritdoc />
    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var executionDirectory = Path.Combine(_options.WorkingRoot, Guid.CreateVersion7().ToString("n"));
        var stagingDirectory = Path.Combine(executionDirectory, "staging");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            WriteCommandScript(request, stagingDirectory);

            if (request.WorkspaceKey is { } key)
            {
                var workspacesDirectory = Path.Combine(_options.WorkingRoot, "workspaces");
                Directory.CreateDirectory(workspacesDirectory);
                var workspaceImage = Path.GetFullPath(
                    Path.Combine(workspacesDirectory, SafePathNames.For(key) + ".ext4")
                );

                var gate = WorkspaceGates.GetOrAdd(workspaceImage, static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (File.Exists(workspaceImage))
                    {
                        var scriptPath = Path.Combine(stagingDirectory, ".orkis", "command.sh");
                        await InjectCommandScriptAsync(workspaceImage, scriptPath, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await BuildScratchImageAsync(stagingDirectory, workspaceImage, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return await BootAndRunAsync(request, executionDirectory, workspaceImage, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }

            var scratchImage = Path.Combine(executionDirectory, "scratch.ext4");
            await BuildScratchImageAsync(stagingDirectory, scratchImage, cancellationToken).ConfigureAwait(false);
            return await BootAndRunAsync(request, executionDirectory, scratchImage, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(executionDirectory);
        }
    }

    private async Task<SandboxExecutionResult> BootAndRunAsync(
        SandboxExecutionRequest request,
        string executionDirectory,
        string scratchImagePath,
        CancellationToken cancellationToken
    )
    {
        var configPath = Path.Combine(executionDirectory, "vm.json");
        await File.WriteAllTextAsync(configPath, BuildVmConfig(scratchImagePath), cancellationToken)
            .ConfigureAwait(false);

        var startInfo = new ProcessStartInfo { FileName = _options.FirecrackerPath };
        startInfo.ArgumentList.Add("--no-api");
        startInfo.ArgumentList.Add("--config-file");
        startInfo.ArgumentList.Add(configPath);

        var outcome = await FirecrackerProcess
            .RunAsync(
                startInfo,
                request.Timeout ?? _options.DefaultTimeout,
                // The console carries boot noise plus both streams; keep headroom.
                (_options.MaxOutputLength * 2) + 4096,
                _timeProvider,
                cancellationToken
            )
            .ConfigureAwait(false);

        return ParseConsole(outcome);
    }

    /// <summary>
    /// Places this execution's command script at /.orkis/command.sh inside an existing
    /// unmounted workspace image, via debugfs so no mount (and no root) is needed.
    /// </summary>
    private async Task InjectCommandScriptAsync(
        string imagePath,
        string scriptPath,
        CancellationToken cancellationToken
    )
    {
        // mkdir and rm fail harmlessly when the directory already exists or the file is
        // absent, and debugfs exits 0 even when a request fails — correctness rests on
        // the stat verification at the end.
        await RunDebugfsAsync(imagePath, "mkdir /.orkis", cancellationToken).ConfigureAwait(false);
        await RunDebugfsAsync(imagePath, "rm /.orkis/command.sh", cancellationToken).ConfigureAwait(false);
        await RunDebugfsAsync(imagePath, $"write {scriptPath} /.orkis/command.sh", cancellationToken)
            .ConfigureAwait(false);

        var stat = await RunDebugfsAsync(imagePath, "stat /.orkis/command.sh", cancellationToken)
            .ConfigureAwait(false);
        if (!stat.StandardOutput.Contains("Type: regular", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Injecting the command script into workspace image '{imagePath}' failed: {stat.StandardError}"
            );
        }
    }

    private async Task<FirecrackerProcess.Outcome> RunDebugfsAsync(
        string imagePath,
        string requestText,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo { FileName = _options.DebugfsPath };
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("-R");
        startInfo.ArgumentList.Add(requestText);
        startInfo.ArgumentList.Add(imagePath);

        return await FirecrackerProcess
            .RunAsync(startInfo, TimeSpan.FromSeconds(30), 8192, _timeProvider, cancellationToken)
            .ConfigureAwait(false);
    }

    private void WriteCommandScript(SandboxExecutionRequest request, string stagingDirectory)
    {
        var script = new StringBuilder();

        foreach (var name in _options.EnvironmentAllowlist)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                script.Append("export ").Append(name).Append('=').AppendLine(ShellQuote(value));
            }
        }

        if (request.WorkingDirectory is { } requested)
        {
            if (Path.IsPathRooted(requested) || requested.Split('/', '\\').Contains(".."))
            {
                throw new ArgumentException(
                    "The working directory must be a relative path inside the sandbox.",
                    nameof(request)
                );
            }

            Directory.CreateDirectory(Path.Combine(stagingDirectory, requested));
            script.Append("mkdir -p ").AppendLine(ShellQuote(requested));
            script.Append("cd ").AppendLine(ShellQuote(requested));
        }

        script.Append("exec ").Append(ShellQuote(request.Executable));
        foreach (var argument in request.Arguments)
        {
            script.Append(' ').Append(ShellQuote(argument));
        }

        script.AppendLine();

        var orkisDirectory = Path.Combine(stagingDirectory, ".orkis");
        Directory.CreateDirectory(orkisDirectory);
        File.WriteAllText(Path.Combine(orkisDirectory, "command.sh"), script.ToString());
    }

    private async Task BuildScratchImageAsync(
        string stagingDirectory,
        string imagePath,
        CancellationToken cancellationToken
    )
    {
        await using (var image = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write))
        {
            image.SetLength(_options.ScratchSizeMib * 1024L * 1024L);
        }

        var startInfo = new ProcessStartInfo { FileName = _options.MkfsPath };
        foreach (var argument in (string[])["-q", "-F", "-d", stagingDirectory, imagePath])
        {
            startInfo.ArgumentList.Add(argument);
        }

        var outcome = await FirecrackerProcess
            .RunAsync(startInfo, TimeSpan.FromSeconds(30), 8192, _timeProvider, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.ExitCode != 0)
        {
            throw new InvalidOperationException($"Building the scratch image failed: {outcome.StandardError}");
        }
    }

    private string BuildVmConfig(string scratchImagePath)
    {
        var config = new Dictionary<string, object>
        {
            ["boot-source"] = new Dictionary<string, object>
            {
                ["kernel_image_path"] = Path.GetFullPath(_options.KernelImagePath),
                ["boot_args"] = "console=ttyS0 reboot=k panic=1 pci=off quiet loglevel=0 init=/init",
            },
            ["drives"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["drive_id"] = "rootfs",
                    ["path_on_host"] = Path.GetFullPath(_options.RootfsImagePath),
                    ["is_root_device"] = true,
                    ["is_read_only"] = true,
                },
                new Dictionary<string, object>
                {
                    ["drive_id"] = "scratch",
                    ["path_on_host"] = Path.GetFullPath(scratchImagePath),
                    ["is_root_device"] = false,
                    ["is_read_only"] = false,
                },
            },
            // No "network-interfaces": NetworkPolicy.None means the VM has no NIC at all.
            ["machine-config"] = new Dictionary<string, object>
            {
                ["vcpu_count"] = _options.VcpuCount,
                ["mem_size_mib"] = _options.MemorySizeMib,
                ["smt"] = false,
            },
        };
        return JsonSerializer.Serialize(config);
    }

    private SandboxExecutionResult ParseConsole(FirecrackerProcess.Outcome outcome)
    {
        var console = outcome.StandardOutput;
        var stdoutStart = console.IndexOf(StdoutMarker, StringComparison.Ordinal);
        var stderrStart = console.IndexOf(StderrMarker, StringComparison.Ordinal);
        var exitStart = console.IndexOf(ExitMarker, StringComparison.Ordinal);

        if (stdoutStart < 0 || stderrStart < stdoutStart || exitStart < stderrStart)
        {
            // The guest never reached the /init contract: boot failure, kernel panic, or timeout.
            return new SandboxExecutionResult
            {
                ExitCode =
                    outcome.TimedOut ? -1
                    : outcome.ExitCode == 0 ? -1
                    : outcome.ExitCode,
                StandardOutput = "",
                StandardError = Cap(
                    outcome.TimedOut
                        ? "The micro-VM timed out before completing."
                        : $"The micro-VM did not produce a result. Console: {console}\n{outcome.StandardError}"
                ),
                TimedOut = outcome.TimedOut,
            };
        }

        var stdout = Slice(console, stdoutStart + StdoutMarker.Length, stderrStart);
        var stderr = Slice(console, stderrStart + StderrMarker.Length, exitStart);

        var exitLine = console[(exitStart + ExitMarker.Length)..];
        var exitEnd = exitLine.IndexOf("===", StringComparison.Ordinal);
        var exitCode = exitEnd > 0 && int.TryParse(exitLine[..exitEnd], out var parsed) ? parsed : -1;

        return new SandboxExecutionResult
        {
            ExitCode = exitCode,
            StandardOutput = Cap(stdout),
            StandardError = Cap(stderr),
            // All markers present means the command itself completed; a timeout at
            // this point was only the VM failing to shut down promptly.
            TimedOut = false,
        };
    }

    private string Cap(string value) =>
        value.Length <= _options.MaxOutputLength ? value : value[.._options.MaxOutputLength];

    // The serial console transmits CRLF; normalize back to the guest's newlines.
    private static string Slice(string console, int start, int end) =>
        console[start..end].Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n');

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>Probes whether Firecracker sandboxing can work here: Linux, KVM access, and the binary.</summary>
    public static bool IsSupported(string firecrackerPath = "firecracker")
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/kvm"))
        {
            return false;
        }

        try
        {
            using var kvm = File.Open("/dev/kvm", FileMode.Open, FileAccess.ReadWrite);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(':') ?? [];
        return Path.IsPathRooted(firecrackerPath)
            ? File.Exists(firecrackerPath)
            : paths.Any(p => File.Exists(Path.Combine(p, firecrackerPath)));
    }
}
