using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Orkis.Sandboxing;

/// <summary>
/// Executes each command in its own Firecracker micro-VM: a hardware-virtualized
/// boundary (KVM) with a read-only root filesystem, a private writable /work drive,
/// and no network device. Without a <see cref="SandboxExecutionRequest.WorkspaceKey"/>
/// each command gets a throwaway VM and scratch. With one, /work is a persistent
/// per-key image — and when the rootfs carries the Orkis guest agent, one warm VM per
/// workspace serves successive commands over vsock: boot cost is paid once, in-memory
/// state persists between commands, and concurrent commands are ordinary in-OS
/// concurrency. Warm VMs shut down after an idle timeout; only memory state is lost.
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
public sealed class FirecrackerSandbox : ISandbox, IWorkspaceFileAccess, IAsyncDisposable
{
    private const string StdoutMarker = "===ORKIS:STDOUT===";
    private const string StderrMarker = "===ORKIS:STDERR===";
    private const string ExitMarker = "===ORKIS:EXIT:";

    // A rw ext4 image attached to two VMs at once corrupts; executions sharing a
    // persistent workspace serialize on a per-image gate. In-VM concurrency (one
    // machine, many processes) is the model's business; two machines on one disk
    // is not survivable and is ours.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WorkspaceGates = new();

    private readonly ConcurrentDictionary<string, FirecrackerWarmVm> _warmVms = new();
    private readonly FirecrackerSandboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private volatile bool _agentUnavailable;

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

        if (_options.Network.Mode == NetworkMode.Allowlist)
        {
            throw new NotSupportedException(
                "NetworkMode.Allowlist is not yet implemented; use None or RestrictedEgress."
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
            if (request.WorkspaceKey is { } key)
            {
                return await ExecutePersistentAsync(
                        request,
                        key,
                        executionDirectory,
                        stagingDirectory,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            WriteCommandScript(request, stagingDirectory);
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

    private async Task<SandboxExecutionResult> ExecutePersistentAsync(
        SandboxExecutionRequest request,
        string key,
        string executionDirectory,
        string stagingDirectory,
        CancellationToken cancellationToken
    )
    {
        var workspaceImage = GetWorkspaceImagePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceImage)!);
        var gate = WorkspaceGates.GetOrAdd(workspaceImage, static _ => new SemaphoreSlim(1, 1));

        FirecrackerWarmVm? warmVm = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_options.EnableWarmVms && !_agentUnavailable)
            {
                warmVm = await GetOrBootWarmVmAsync(workspaceImage, stagingDirectory, cancellationToken)
                    .ConfigureAwait(false);
                warmVm?.BeginCommand();
            }

            if (warmVm is null)
            {
                return await ExecuteColdLockedAsync(
                        request,
                        workspaceImage,
                        executionDirectory,
                        stagingDirectory,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }

        try
        {
            return await ExecuteViaAgentAsync(warmVm, request, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Connecting to the agent failed, so the command never started: discard the
            // VM and run this command the cold way instead.
            await RemoveWarmVmAsync(workspaceImage, warmVm).ConfigureAwait(false);

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ExecuteColdLockedAsync(
                        request,
                        workspaceImage,
                        executionDirectory,
                        stagingDirectory,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
            when (ex is IOException or JsonException or FormatException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            )
        {
            // The VM broke mid-command. Whether the command's side effects happened is
            // unknowable, so do not silently re-execute — surface the loss instead.
            await RemoveWarmVmAsync(workspaceImage, warmVm).ConfigureAwait(false);
            return new SandboxExecutionResult
            {
                ExitCode = -1,
                StandardOutput = "",
                StandardError =
                    "The warm micro-VM stopped responding mid-command and was discarded. Workspace "
                    + "files are preserved; retry if the command is safe to run again.",
                TimedOut = ex is OperationCanceledException,
            };
        }
        finally
        {
            warmVm.EndCommand();
        }
    }

    private async Task<SandboxExecutionResult> ExecuteColdLockedAsync(
        SandboxExecutionRequest request,
        string workspaceImage,
        string executionDirectory,
        string stagingDirectory,
        CancellationToken cancellationToken
    )
    {
        WriteCommandScript(request, stagingDirectory);
        if (File.Exists(workspaceImage))
        {
            await ReplayJournalAsync(workspaceImage, cancellationToken).ConfigureAwait(false);
            var scriptPath = Path.Combine(stagingDirectory, ".orkis", "command.sh");
            await InjectCommandScriptAsync(workspaceImage, scriptPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await BuildScratchImageAsync(stagingDirectory, workspaceImage, cancellationToken).ConfigureAwait(false);
        }

        return await BootAndRunAsync(request, executionDirectory, workspaceImage, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the workspace's warm VM, booting one if needed. Must be called holding
    /// the workspace gate. Returns <see langword="null"/> (and stops trying for this
    /// sandbox) when the rootfs has no guest agent.
    /// </summary>
    private async Task<FirecrackerWarmVm?> GetOrBootWarmVmAsync(
        string workspaceImage,
        string stagingDirectory,
        CancellationToken cancellationToken
    )
    {
        if (_warmVms.TryGetValue(workspaceImage, out var existing))
        {
            return existing;
        }

        if (File.Exists(workspaceImage))
        {
            await ReplayJournalAsync(workspaceImage, cancellationToken).ConfigureAwait(false);

            // A stale command script from an earlier cold run must not re-execute if
            // this boot falls back to the legacy init flow (rootfs without an agent).
            await RunDebugfsAsync(workspaceImage, "rm /.orkis/command.sh", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // The staging directory has no command script at this point, so this
            // builds an empty workspace.
            await BuildScratchImageAsync(stagingDirectory, workspaceImage, cancellationToken).ConfigureAwait(false);
        }

        var vmDirectory = Path.Combine(_options.WorkingRoot, "vms", Guid.CreateVersion7().ToString("n"));
        var networkLease = AcquireNetworkLease();
        string vmConfig;
        try
        {
            vmConfig = BuildVmConfig(workspaceImage, Path.Combine(vmDirectory, "vsock.sock"), networkLease);
        }
        catch
        {
            networkLease?.Dispose();
            throw;
        }

        // TryBootAsync owns the lease from here: released on boot failure, or with the
        // VM's disposal — a warm VM keeps its TAP for its whole lifetime.
        var vm = await FirecrackerWarmVm
            .TryBootAsync(_options, vmConfig, vmDirectory, HandleVmIdle, _timeProvider, networkLease, cancellationToken)
            .ConfigureAwait(false);
        if (vm is null)
        {
            _agentUnavailable = true;
            return null;
        }

        _warmVms[workspaceImage] = vm;
        return vm;
    }

    private async Task<SandboxExecutionResult> ExecuteViaAgentAsync(
        FirecrackerWarmVm vm,
        SandboxExecutionRequest request,
        CancellationToken cancellationToken
    )
    {
        var timeout = request.Timeout ?? _options.DefaultTimeout;

        var payload = new Dictionary<string, object?>
        {
            ["command"] = BuildCommandLine(request),
            ["timeoutSeconds"] = Math.Max(1, (int)timeout.TotalSeconds),
        };
        if (ValidateWorkingDirectory(request) is { } workingDirectory)
        {
            payload["cwd"] = workingDirectory;
        }

        var environment = new Dictionary<string, string>();
        foreach (var name in _options.EnvironmentAllowlist)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                environment[name] = value;
            }
        }

        if (environment.Count > 0)
        {
            payload["env"] = environment;
        }

        using var response = await vm.SendRequestAsync(
                JsonSerializer.Serialize(payload),
                timeout + TimeSpan.FromSeconds(15),
                cancellationToken
            )
            .ConfigureAwait(false);

        var root = response.RootElement;
        return new SandboxExecutionResult
        {
            ExitCode = root.GetProperty("exit").GetInt32(),
            StandardOutput = Cap(DecodeBase64(root, "stdout")),
            StandardError = Cap(DecodeBase64(root, "stderr")),
            TimedOut = root.TryGetProperty("timedOut", out var timedOut) && timedOut.GetBoolean(),
        };
    }

    private void HandleVmIdle(FirecrackerWarmVm vm) =>
        _ = Task.Run(async () =>
        {
            var entry = _warmVms.FirstOrDefault(pair => ReferenceEquals(pair.Value, vm));
            if (entry.Key is null)
            {
                return;
            }

            var gate = WorkspaceGates.GetOrAdd(entry.Key, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (vm.InFlight == 0 && _warmVms.TryRemove(KeyValuePair.Create(entry.Key, vm)))
                {
                    await vm.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        });

    private async Task RemoveWarmVmAsync(string workspaceImage, FirecrackerWarmVm vm)
    {
        _warmVms.TryRemove(KeyValuePair.Create(workspaceImage, vm));
        await vm.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var key in _warmVms.Keys.ToList())
        {
            if (_warmVms.TryRemove(key, out var vm))
            {
                await vm.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string DecodeBase64(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.GetString() is { Length: > 0 } encoded
            ? Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
            : "";

    private static string BuildCommandLine(SandboxExecutionRequest request)
    {
        var builder = new StringBuilder(ShellQuote(request.Executable));
        foreach (var argument in request.Arguments)
        {
            builder.Append(' ').Append(ShellQuote(argument));
        }

        return builder.ToString();
    }

    private async Task<SandboxExecutionResult> BootAndRunAsync(
        SandboxExecutionRequest request,
        string executionDirectory,
        string scratchImagePath,
        CancellationToken cancellationToken
    )
    {
        // The lease outlives the whole VM run; disposing it frees the TAP for reuse.
        using var networkLease = AcquireNetworkLease();

        var configPath = Path.Combine(executionDirectory, "vm.json");
        await File.WriteAllTextAsync(
                configPath,
                BuildVmConfig(scratchImagePath, vsockUdsPath: null, networkLease),
                cancellationToken
            )
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
    private Task InjectCommandScriptAsync(string imagePath, string scriptPath, CancellationToken cancellationToken) =>
        InjectFileAsync(imagePath, scriptPath, ".orkis/command.sh", cancellationToken);

    /// <summary>Places a host file at a path inside an unmounted ext4 image via debugfs.</summary>
    private async Task InjectFileAsync(
        string imagePath,
        string hostFilePath,
        string imageRelativePath,
        CancellationToken cancellationToken
    )
    {
        // mkdir and rm fail harmlessly when the directory already exists or the file is
        // absent, and debugfs exits 0 even when a request fails — correctness rests on
        // the stat verification at the end.
        var segments = imageRelativePath.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var parent = string.Join('/', segments[..i]);
            await RunDebugfsAsync(imagePath, $"mkdir /{parent}", cancellationToken).ConfigureAwait(false);
        }

        await RunDebugfsAsync(imagePath, $"rm /{imageRelativePath}", cancellationToken).ConfigureAwait(false);
        await RunDebugfsAsync(imagePath, $"write {hostFilePath} /{imageRelativePath}", cancellationToken)
            .ConfigureAwait(false);

        var stat = await RunDebugfsAsync(imagePath, $"stat /{imageRelativePath}", cancellationToken)
            .ConfigureAwait(false);
        if (!stat.StandardOutput.Contains("Type: regular", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Writing '{imageRelativePath}' into workspace image '{imagePath}' failed: {stat.StandardError}"
            );
        }
    }

    /// <inheritdoc />
    public async Task<Stream?> ReadWorkspaceFileAsync(
        string workspaceKey,
        string relativePath,
        CancellationToken cancellationToken = default
    )
    {
        var imageRelativePath = ValidateWorkspaceRelativePath(relativePath);
        ArgumentException.ThrowIfNullOrEmpty(workspaceKey);

        var imagePath = GetWorkspaceImagePath(workspaceKey);
        if (!File.Exists(imagePath))
        {
            return null;
        }

        var gate = WorkspaceGates.GetOrAdd(imagePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // debugfs must never touch an image a live VM has attached: shut a warm VM
            // down first (its clean unmount also leaves the journal clean).
            if (_warmVms.TryRemove(imagePath, out var warmVm))
            {
                await warmVm.DisposeAsync().ConfigureAwait(false);
            }

            await ReplayJournalAsync(imagePath, cancellationToken).ConfigureAwait(false);
            var tempPath = Path.Combine(Path.GetTempPath(), "orkis-dump-" + Guid.CreateVersion7().ToString("n"));
            try
            {
                await RunDebugfsAsync(imagePath, $"dump /{imageRelativePath} {tempPath}", cancellationToken)
                    .ConfigureAwait(false);
                if (!File.Exists(tempPath))
                {
                    return null;
                }

                var bytes = await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false);
                return new MemoryStream(bytes, writable: false);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task WriteWorkspaceFileAsync(
        string workspaceKey,
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default
    )
    {
        var imageRelativePath = ValidateWorkspaceRelativePath(relativePath);
        ArgumentException.ThrowIfNullOrEmpty(workspaceKey);
        ArgumentNullException.ThrowIfNull(content);

        var imagePath = GetWorkspaceImagePath(workspaceKey);
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);

        var gate = WorkspaceGates.GetOrAdd(imagePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // debugfs must never touch an image a live VM has attached: shut a warm VM
            // down first (its clean unmount also leaves the journal clean).
            if (_warmVms.TryRemove(imagePath, out var warmVm))
            {
                await warmVm.DisposeAsync().ConfigureAwait(false);
            }

            if (!File.Exists(imagePath))
            {
                var emptyStaging = Path.Combine(
                    Path.GetTempPath(),
                    "orkis-staging-" + Guid.CreateVersion7().ToString("n")
                );
                Directory.CreateDirectory(emptyStaging);
                try
                {
                    await BuildScratchImageAsync(emptyStaging, imagePath, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    TryDeleteDirectory(emptyStaging);
                }
            }
            else
            {
                await ReplayJournalAsync(imagePath, cancellationToken).ConfigureAwait(false);
            }

            var tempPath = Path.Combine(Path.GetTempPath(), "orkis-stage-" + Guid.CreateVersion7().ToString("n"));
            try
            {
                var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using (stream.ConfigureAwait(false))
                {
                    await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                await InjectFileAsync(imagePath, tempPath, imageRelativePath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetWorkspaceImagePath(string workspaceKey) =>
        Path.GetFullPath(Path.Combine(_options.WorkingRoot, "workspaces", SafePathNames.For(workspaceKey) + ".ext4"));

    private static string ValidateWorkspaceRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(relativePath) || normalized.Split('/').Contains(".."))
        {
            throw new ArgumentException(
                "The path must be relative and inside the workspace.",
                nameof(relativePath)
            );
        }

        return normalized;
    }

    /// <summary>
    /// Replays the image's ext4 journal (e2fsck preen) so debugfs sees current
    /// metadata. A guest that halts without a clean unmount leaves the journal dirty,
    /// and debugfs neither replays nor updates a journal — skipping this would lose
    /// debugfs's changes to the kernel's own replay at the next mount.
    /// </summary>
    private async Task ReplayJournalAsync(string imagePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo { FileName = _options.E2fsckPath };
        startInfo.ArgumentList.Add("-fp");
        startInfo.ArgumentList.Add(imagePath);

        var outcome = await FirecrackerProcess
            .RunAsync(startInfo, TimeSpan.FromSeconds(60), 8192, _timeProvider, cancellationToken)
            .ConfigureAwait(false);

        // 0 = clean, 1 = errors corrected, 2 = corrected with reboot advised (moot for
        // an unmounted image); anything higher means the image is genuinely damaged.
        if (outcome.ExitCode > 2)
        {
            throw new InvalidOperationException(
                $"Repairing workspace image '{imagePath}' failed (e2fsck exit {outcome.ExitCode}): {outcome.StandardError}"
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

        if (ValidateWorkingDirectory(request) is { } requested)
        {
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

    private static string? ValidateWorkingDirectory(SandboxExecutionRequest request)
    {
        if (request.WorkingDirectory is not { } requested)
        {
            return null;
        }

        if (Path.IsPathRooted(requested) || requested.Split('/', '\\').Contains(".."))
        {
            throw new ArgumentException(
                "The working directory must be a relative path inside the sandbox.",
                nameof(request)
            );
        }

        return requested;
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

    /// <summary>
    /// Acquires a TAP lease when the policy grants network access, or
    /// <see langword="null"/> for <see cref="NetworkMode.None"/>.
    /// </summary>
    private TapLease? AcquireNetworkLease()
    {
        if (_options.Network.Mode != NetworkMode.RestrictedEgress)
        {
            return null;
        }

        return TapLease.TryAcquire(_options.TapDevicePrefix, _options.TapPoolSize)
            ?? throw new InvalidOperationException(
                "RestrictedEgress needs a free pre-provisioned TAP device and none was available. "
                    + "Run scripts/setup-firecracker-network.sh once (with sudo), or reduce concurrent "
                    + "networked VMs."
            );
    }

    private string BuildVmConfig(string scratchImagePath, string? vsockUdsPath = null, TapLease? network = null)
    {
        var bootArgs = "console=ttyS0 reboot=k panic=1 pci=off quiet loglevel=0 init=/init";
        if (vsockUdsPath is not null)
        {
            bootArgs += " orkis.mode=agent";
        }

        if (network is not null)
        {
            // The guest's static address derives from its TAP index; /init applies it.
            bootArgs +=
                $" orkis.net={_options.GuestSubnetPrefix}.{network.Index + 2}/24:{_options.GuestSubnetPrefix}.1";
        }

        var config = new Dictionary<string, object>
        {
            ["boot-source"] = new Dictionary<string, object>
            {
                ["kernel_image_path"] = Path.GetFullPath(_options.KernelImagePath),
                ["boot_args"] = bootArgs,
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
            // With NetworkPolicy.None the VM has no NIC at all; "network-interfaces"
            // is added below only for RestrictedEgress.
            ["machine-config"] = new Dictionary<string, object>
            {
                ["vcpu_count"] = _options.VcpuCount,
                ["mem_size_mib"] = _options.MemorySizeMib,
                ["smt"] = false,
            },
        };
        if (network is not null)
        {
            config["network-interfaces"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["iface_id"] = "eth0",
                    ["host_dev_name"] = network.DeviceName,
                    ["guest_mac"] = $"06:6F:72:6B:00:{network.Index:X2}",
                },
            };
        }

        if (vsockUdsPath is not null)
        {
            config["vsock"] = new Dictionary<string, object>
            {
                ["guest_cid"] = 3,
                ["uds_path"] = Path.GetFullPath(vsockUdsPath),
            };
        }

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
