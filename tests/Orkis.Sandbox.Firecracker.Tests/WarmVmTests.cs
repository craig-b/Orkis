using System.Diagnostics;
using Microsoft.Extensions.Options;
using Orkis.Sandboxing;

namespace Orkis.Sandbox.Firecracker.Tests;

/// <summary>
/// Builds a copy of the deployed rootfs with the current scripts/guest/ init and
/// agent injected via debugfs, so warm-VM tests run against the repo's guest code
/// without needing network access to rebuild a rootfs from scratch.
/// </summary>
public sealed class PatchedRootfsFixture : IDisposable
{
    public string? RootfsPath { get; }

    public PatchedRootfsFixture()
    {
        if (!WarmVmTests.BaseAssetsAvailable)
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var patched = Path.Combine(Path.GetTempPath(), $"orkis-rootfs-agent-{Guid.NewGuid():n}.ext4");
        File.Copy(WarmVmTests.DeployedRootfsPath, patched);

        Debugfs(patched, "rm /init");
        Debugfs(patched, $"write {Path.Combine(repoRoot, "scripts", "guest", "init.sh")} /init");
        Debugfs(patched, "sif /init mode 0100755");
        Debugfs(patched, "mkdir /opt");
        Debugfs(patched, $"write {Path.Combine(repoRoot, "scripts", "guest", "orkis-agent.py")} /opt/orkis-agent.py");

        // DNS config must be writable on the read-only rootfs (see setup-firecracker.sh).
        Debugfs(patched, "rm /etc/resolv.conf");
        Debugfs(patched, "symlink /etc/resolv.conf /tmp/resolv.conf");

        RootfsPath = patched;
    }

    public void Dispose()
    {
        if (RootfsPath is not null && File.Exists(RootfsPath))
        {
            File.Delete(RootfsPath);
        }
    }

    private static void Debugfs(string imagePath, string request)
    {
        var startInfo = new ProcessStartInfo { FileName = "debugfs", RedirectStandardError = true };
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("-R");
        startInfo.ArgumentList.Add(request);
        startInfo.ArgumentList.Add(imagePath);
        using var process = Process.Start(startInfo)!;
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orkis.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}

// These tests boot real micro-VMs and self-skip (pass vacuously) where KVM, the
// firecracker binary, or the guest images (scripts/setup-firecracker.sh) are absent.
public sealed class WarmVmTests(PatchedRootfsFixture fixture) : IClassFixture<PatchedRootfsFixture>, IAsyncLifetime
{
    internal static readonly string AssetHome =
        Environment.GetEnvironmentVariable("ORKIS_FIRECRACKER_HOME")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "orkis",
            "firecracker"
        );

    internal static readonly string KernelPath = Path.GetFullPath(Path.Combine(AssetHome, "vmlinux"));
    internal static readonly string DeployedRootfsPath = Path.GetFullPath(Path.Combine(AssetHome, "rootfs.ext4"));

    internal static bool BaseAssetsAvailable =>
        FirecrackerSandbox.IsSupported() && File.Exists(KernelPath) && File.Exists(DeployedRootfsPath);

    private bool Available => BaseAssetsAvailable && fixture.RootfsPath is not null;

    private readonly string _workingRoot = Path.Combine(Path.GetTempPath(), $"orkis-warm-tests-{Guid.NewGuid():n}");
    private readonly List<FirecrackerSandbox> _sandboxes = [];

    public Task InitializeAsync() => Task.CompletedTask;

    // IAsyncLifetime, not IAsyncDisposable: xUnit v2 only awaits the former, and warm
    // VMs outlive the test process (becoming orphaned VMMs) if teardown never runs.
    public async Task DisposeAsync()
    {
        foreach (var sandbox in _sandboxes)
        {
            await sandbox.DisposeAsync();
        }

        if (Directory.Exists(_workingRoot))
        {
            Directory.Delete(_workingRoot, recursive: true);
        }
    }

    private FirecrackerSandbox CreateSandbox(Action<FirecrackerSandboxOptions>? configure = null)
    {
        var options = new FirecrackerSandboxOptions
        {
            KernelImagePath = KernelPath,
            RootfsImagePath = fixture.RootfsPath!,
            WorkingRoot = _workingRoot,
        };
        configure?.Invoke(options);
        var sandbox = new FirecrackerSandbox(Options.Create(options));
        _sandboxes.Add(sandbox);
        return sandbox;
    }

    private static SandboxExecutionRequest Shell(string script, string workspaceKey = "chat-1") =>
        new()
        {
            Executable = "/bin/sh",
            Arguments = ["-c", script],
            WorkspaceKey = workspaceKey,
        };

    [Fact]
    public async Task CommandsReuseOneWarmVm()
    {
        if (!Available)
        {
            return;
        }

        var sandbox = CreateSandbox();

        // /tmp is tmpfs — memory only — so seeing the marker proves the same VM.
        var first = await sandbox.ExecuteAsync(
            Shell("test -f /tmp/warm-marker && echo reused || { touch /tmp/warm-marker; echo first; }")
        );
        Assert.Equal(0, first.ExitCode);
        Assert.Equal("first", first.StandardOutput.Trim());

        var second = await sandbox.ExecuteAsync(
            Shell("test -f /tmp/warm-marker && echo reused || { touch /tmp/warm-marker; echo first; }")
        );
        Assert.Equal(0, second.ExitCode);
        Assert.Equal("reused", second.StandardOutput.Trim());
    }

    [Fact]
    public async Task WorkspaceFilesFlowBetweenWarmCommands()
    {
        if (!Available)
        {
            return;
        }

        var sandbox = CreateSandbox();

        var write = await sandbox.ExecuteAsync(Shell("printf warm-disk-state > note.txt"));
        Assert.Equal(0, write.ExitCode);

        var read = await sandbox.ExecuteAsync(Shell("cat note.txt"));
        Assert.Equal(0, read.ExitCode);
        Assert.Equal("warm-disk-state", read.StandardOutput.Trim());

        var isolated = await sandbox.ExecuteAsync(Shell("cat note.txt", workspaceKey: "chat-2"));
        Assert.NotEqual(0, isolated.ExitCode);
    }

    [Fact]
    public async Task ConcurrentCommandsRunInTheSameVm()
    {
        if (!Available)
        {
            return;
        }

        var sandbox = CreateSandbox();

        var results = await Task.WhenAll(
            sandbox.ExecuteAsync(Shell("echo one >> /tmp/concurrent; sleep 1; echo a")),
            sandbox.ExecuteAsync(Shell("echo two >> /tmp/concurrent; sleep 1; echo b"))
        );
        Assert.All(results, result => Assert.Equal(0, result.ExitCode));

        var count = await sandbox.ExecuteAsync(Shell("wc -l < /tmp/concurrent"));
        Assert.Equal("2", count.StandardOutput.Trim());
    }

    [Fact]
    public async Task IdleTimeoutShutsTheVmDownButDiskSurvives()
    {
        if (!Available)
        {
            return;
        }

        var sandbox = CreateSandbox(options => options.WarmVmIdleTimeout = TimeSpan.FromSeconds(2));

        var first = await sandbox.ExecuteAsync(Shell("touch /tmp/warm-marker; printf kept > note.txt"));
        Assert.Equal(0, first.ExitCode);

        await Task.Delay(TimeSpan.FromSeconds(5));

        // A fresh VM: the tmpfs marker is gone, the workspace file is not.
        var second = await sandbox.ExecuteAsync(
            Shell("test -f /tmp/warm-marker && echo memory-survived || echo memory-gone; cat note.txt")
        );
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(["memory-gone", "kept"], second.StandardOutput.Trim().Split('\n'));
    }

    [Fact]
    public async Task FallsBackToColdExecutionWhenRootfsHasNoAgent()
    {
        if (!BaseAssetsAvailable)
        {
            return;
        }

        // The deployed rootfs predates the guest agent; warm boot must fail fast and
        // the command still run via the boot-per-command path.
        var sandbox = CreateSandbox(options =>
        {
            options.RootfsImagePath = DeployedRootfsPath;
            options.AgentReadyTimeout = TimeSpan.FromSeconds(5);
        });

        var result = await sandbox.ExecuteAsync(Shell("echo cold-fallback-works"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("cold-fallback-works", result.StandardOutput.Trim());
    }
}
