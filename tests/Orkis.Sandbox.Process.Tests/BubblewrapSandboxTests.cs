using Microsoft.Extensions.Options;
using Orkis.Sandboxing;

namespace Orkis.Sandbox.Process.Tests;

// These tests exercise real bubblewrap sandboxes and self-skip (pass vacuously)
// where bwrap or unprivileged user namespaces are unavailable, e.g. some CI runners.
public sealed class BubblewrapSandboxTests
{
    private static readonly Task<bool> Supported = BubblewrapSandbox.IsSupportedAsync();

    private static BubblewrapSandbox CreateSandbox(Action<BubblewrapSandboxOptions>? configure = null)
    {
        var options = new BubblewrapSandboxOptions();
        configure?.Invoke(options);
        return new BubblewrapSandbox(Options.Create(options));
    }

    private static SandboxExecutionRequest Shell(string script, TimeSpan? timeout = null) =>
        new()
        {
            Executable = "/bin/sh",
            Arguments = ["-c", script],
            Timeout = timeout,
        };

    [Fact]
    public void ReportsStrictIsolationLevel() => Assert.Equal(SandboxLevel.Strict, CreateSandbox().Level);

    [Fact]
    public async Task RunsCommandsInsideTheSandboxWorkDirectory()
    {
        if (!await Supported)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("echo hello; pwd"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["hello", "/work"], result.StandardOutput.Trim().Split('\n'));
    }

    [Fact]
    public async Task HasNoNetworkByDefault()
    {
        if (!await Supported)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("ls /sys/class/net"));

        // A fresh network namespace contains only the loopback device.
        Assert.Equal("lo", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task SystemDirectoriesAreReadOnly()
    {
        if (!await Supported)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("touch /usr/orkis-test 2>&1"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Read-only", result.StandardOutput + result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HostHomeDirectoriesAreInvisible()
    {
        if (!await Supported)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("test -e /home || echo no-home"));

        Assert.Equal("no-home", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task HostEnvironmentIsStripped()
    {
        if (!await Supported)
        {
            return;
        }

        Environment.SetEnvironmentVariable("ORKIS_BWRAP_SECRET", "leaked");
        try
        {
            var result = await CreateSandbox().ExecuteAsync(Shell("printenv ORKIS_BWRAP_SECRET"));
            Assert.NotEqual(0, result.ExitCode);

            var home = await CreateSandbox().ExecuteAsync(Shell("printenv HOME"));
            Assert.Equal("/work", home.StandardOutput.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORKIS_BWRAP_SECRET", null);
        }
    }

    [Fact]
    public async Task KillsProcessOnTimeout()
    {
        if (!await Supported)
        {
            return;
        }

        var result = await CreateSandbox().ExecuteAsync(Shell("sleep 30", timeout: TimeSpan.FromMilliseconds(500)));

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task RelativeWorkingDirectoryMapsInsideWork()
    {
        if (!await Supported)
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
