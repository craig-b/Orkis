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
            await using var sandbox = new FirecrackerSandbox(
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
    public async Task WorkspaceFileAccessRoundTripsThroughTheImage()
    {
        if (!Available)
        {
            return;
        }

        var workingRoot = Path.Combine(Path.GetTempPath(), $"orkis-fc-wfa-{Guid.NewGuid():n}");
        try
        {
            await using var sandbox = new FirecrackerSandbox(
                Options.Create(
                    new FirecrackerSandboxOptions
                    {
                        KernelImagePath = KernelPath,
                        RootfsImagePath = RootfsPath,
                        WorkingRoot = workingRoot,
                    }
                )
            );

            // Stage a file into the (not yet existing) workspace image, then read it from a VM.
            using (var content = new MemoryStream("staged-for-vm"u8.ToArray()))
            {
                await sandbox.WriteWorkspaceFileAsync("chat-1", "input/data.txt", content);
            }

            var read = await sandbox.ExecuteAsync(Shell("cat input/data.txt") with { WorkspaceKey = "chat-1" });
            Assert.Equal(0, read.ExitCode);
            Assert.Equal("staged-for-vm", read.StandardOutput.Trim());

            // Now the reverse: a VM writes a file, the host pulls it out via debugfs.
            var write = await sandbox.ExecuteAsync(
                Shell("printf from-the-vm > produced.txt") with
                {
                    WorkspaceKey = "chat-1",
                }
            );
            Assert.Equal(0, write.ExitCode);

            var stream = await sandbox.ReadWorkspaceFileAsync("chat-1", "produced.txt");
            Assert.NotNull(stream);
            await using (stream)
            {
                using var reader = new StreamReader(stream);
                Assert.Equal("from-the-vm", await reader.ReadToEndAsync());
            }

            Assert.Null(await sandbox.ReadWorkspaceFileAsync("chat-1", "absent.txt"));
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
    public async Task CorruptWorkspaceImageIsEvictedNotFatal()
    {
        if (!Available)
        {
            return;
        }

        var workingRoot = Path.Combine(Path.GetTempPath(), $"orkis-fc-corrupt-{Guid.NewGuid():n}");
        try
        {
            await using var sandbox = new FirecrackerSandbox(
                Options.Create(
                    new FirecrackerSandboxOptions
                    {
                        KernelImagePath = KernelPath,
                        RootfsImagePath = RootfsPath,
                        WorkingRoot = workingRoot,
                    }
                )
            );

            using (var content = new MemoryStream("doomed"u8.ToArray()))
            {
                await sandbox.WriteWorkspaceFileAsync("chat-1", "keep.txt", content);
            }

            // Damage the image beyond what any fsck can fix: overwrite it with noise.
            var imagePath = Assert.Single(Directory.GetFiles(Path.Combine(workingRoot, "workspaces")));
            var noise = new byte[1024 * 1024];
            Random.Shared.NextBytes(noise);
            await File.WriteAllBytesAsync(imagePath, noise);

            // Reading treats the unrepairable image as evicted: the file no longer
            // exists, and the image is deleted rather than left as a dead end.
            Assert.Null(await sandbox.ReadWorkspaceFileAsync("chat-1", "keep.txt"));
            Assert.False(File.Exists(imagePath));

            // The workspace stays usable: staging recreates it empty.
            using (var content = new MemoryStream("fresh"u8.ToArray()))
            {
                await sandbox.WriteWorkspaceFileAsync("chat-1", "fresh.txt", content);
            }

            var stream = await sandbox.ReadWorkspaceFileAsync("chat-1", "fresh.txt");
            Assert.NotNull(stream);
            await stream.DisposeAsync();
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
    public async Task ExecutingOnACorruptImageResetsTheWorkspaceAndSaysSo()
    {
        if (!Available)
        {
            return;
        }

        var workingRoot = Path.Combine(Path.GetTempPath(), $"orkis-fc-reset-{Guid.NewGuid():n}");
        try
        {
            await using var sandbox = new FirecrackerSandbox(
                Options.Create(
                    new FirecrackerSandboxOptions
                    {
                        KernelImagePath = KernelPath,
                        RootfsImagePath = RootfsPath,
                        WorkingRoot = workingRoot,
                    }
                )
            );

            using (var content = new MemoryStream("doomed"u8.ToArray()))
            {
                await sandbox.WriteWorkspaceFileAsync("chat-1", "keep.txt", content);
            }

            var imagePath = Assert.Single(Directory.GetFiles(Path.Combine(workingRoot, "workspaces")));
            var noise = new byte[1024 * 1024];
            Random.Shared.NextBytes(noise);
            await File.WriteAllBytesAsync(imagePath, noise);

            // Execution recreates the workspace empty and tells the model in the
            // result itself, instead of failing with "run fsck manually".
            var result = await sandbox.ExecuteAsync(Shell("ls; echo alive") with { WorkspaceKey = "chat-1" });

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("alive", result.StandardOutput);
            Assert.DoesNotContain("keep.txt", result.StandardOutput);
            Assert.Contains("recreated", result.StandardError);
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
    public async Task StampedGuestImageCarriesNoVersionWarning()
    {
        if (!Available)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("echo ok"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("guest image", result.StandardError);
    }

    [Fact]
    public async Task StaleGuestImageWarnsInEveryResult()
    {
        if (!Available)
        {
            return;
        }

        var unstamped = Path.Combine(Path.GetTempPath(), $"orkis-unstamped-{Guid.NewGuid():n}.ext4");
        File.Copy(RootfsPath, unstamped);
        try
        {
            PatchedRootfsFixture.Debugfs(unstamped, "rm /opt/orkis-guest.version");

            await using var sandbox = new FirecrackerSandbox(
                Options.Create(
                    new FirecrackerSandboxOptions { KernelImagePath = KernelPath, RootfsImagePath = unstamped }
                )
            );
            var result = await sandbox.ExecuteAsync(Shell("echo ok"));

            // The command still runs; the drift warning rides the result.
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("scripts/setup-firecracker.sh", result.StandardError);
        }
        finally
        {
            File.Delete(unstamped);
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
