using Microsoft.Extensions.Options;
using Orkis.Sandboxing;

namespace Orkis.Sandbox.Firecracker.Tests;

// These tests boot real micro-VMs and self-skip (pass vacuously) where KVM, the
// firecracker binary, or the guest images (scripts/setup-firecracker.sh) are absent.
public sealed class FirecrackerSandboxTests
{
    private static readonly string AssetHome =
        Environment.GetEnvironmentVariable("ORKIS_FIRECRACKER_HOME")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "orkis",
            "firecracker"
        );

    private static readonly string KernelPath = Path.GetFullPath(Path.Combine(AssetHome, "vmlinux"));
    private static readonly string RootfsPath = Path.GetFullPath(Path.Combine(AssetHome, "rootfs.ext4"));

    private static bool Available =>
        FirecrackerSandbox.IsSupported() && File.Exists(KernelPath) && File.Exists(RootfsPath);

    private static FirecrackerSandbox CreateSandbox() =>
        new(
            Options.Create(new FirecrackerSandboxOptions { KernelImagePath = KernelPath, RootfsImagePath = RootfsPath })
        );

    private static SandboxExecutionRequest Shell(string script, TimeSpan? timeout = null) =>
        new()
        {
            Executable = "/bin/sh",
            Arguments = ["-c", script],
            Timeout = timeout,
        };

    [Fact]
    public void ReportsStrictIsolationLevel()
    {
        var sandbox = new FirecrackerSandbox(
            Options.Create(new FirecrackerSandboxOptions { KernelImagePath = "/k", RootfsImagePath = "/r" })
        );
        Assert.Equal(SandboxLevel.Strict, sandbox.Level);
    }

    [Fact]
    public async Task WorkspaceFilesPersistAcrossVmBoots()
    {
        if (!Available)
        {
            return;
        }

        var workingRoot = Path.Combine(Path.GetTempPath(), $"orkis-fc-ws-{Guid.NewGuid():n}");
        try
        {
            var sandbox = new FirecrackerSandbox(
                Options.Create(
                    new FirecrackerSandboxOptions
                    {
                        KernelImagePath = KernelPath,
                        RootfsImagePath = RootfsPath,
                        WorkingRoot = workingRoot,
                    }
                )
            );

            var write = await sandbox.ExecuteAsync(
                Shell("echo persisted-across-boots > note.txt") with
                {
                    WorkspaceKey = "chat-1",
                }
            );
            Assert.Equal(0, write.ExitCode);

            // A separate boot of a separate VM on the same workspace image.
            var read = await sandbox.ExecuteAsync(Shell("cat note.txt") with { WorkspaceKey = "chat-1" });

            Assert.Equal(0, read.ExitCode);
            Assert.Equal("persisted-across-boots", read.StandardOutput.Trim());

            var other = await sandbox.ExecuteAsync(Shell("cat note.txt") with { WorkspaceKey = "chat-2" });
            Assert.NotEqual(0, other.ExitCode);
        }
        finally
        {
            if (Directory.Exists(workingRoot))
            {
                Directory.Delete(workingRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunsCommandInMicroVmAndCapturesOutput()
    {
        if (!Available)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("echo hello from the vm; pwd"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["hello from the vm", "/work"], result.StandardOutput.Trim().Split('\n'));
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task SeparatesStderrAndPreservesExitCode()
    {
        if (!Available)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("echo out; echo err 1>&2; exit 7"));

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("out", result.StandardOutput.Trim());
        Assert.Equal("err", result.StandardError.Trim());
    }

    [Fact]
    public async Task GuestHasNoNetworkDevice()
    {
        if (!Available)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("ls /sys/class/net"));

        // No network device is configured for the VM — not even loopback is up.
        Assert.True(string.IsNullOrWhiteSpace(result.StandardOutput) || result.StandardOutput.Trim() == "lo");
    }

    [Fact]
    public async Task RootFilesystemIsReadOnly()
    {
        if (!Available)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("echo x > /bin/evil 2>&1; echo code=$?"));

        Assert.Contains("code=1", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostEnvironmentDoesNotLeakIntoGuest()
    {
        if (!Available)
        {
            return;
        }

        Environment.SetEnvironmentVariable("ORKIS_FC_SECRET", "leaked");
        try
        {
            var result = await CreateSandbox().ExecuteAsync(Shell("printenv ORKIS_FC_SECRET"));
            Assert.NotEqual(0, result.ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORKIS_FC_SECRET", null);
        }
    }

    [Fact]
    public async Task KillsVmOnTimeout()
    {
        if (!Available)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("sleep 60", timeout: TimeSpan.FromSeconds(3)));

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task RelativeWorkingDirectoryMapsIntoWork()
    {
        if (!Available)
        {
            return;
        }

        var request = new SandboxExecutionRequest
        {
            Executable = "/bin/sh",
            Arguments = ["-c", "pwd"],
            WorkingDirectory = "nested/dir",
        };

        var result = await CreateSandbox().ExecuteAsync(request);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/work/nested/dir", result.StandardOutput.Trim());
    }
}
