using Microsoft.Extensions.Options;
using Orkis.Sandboxing;

namespace Orkis.Sandbox.Process.Tests;

// These tests execute real child processes and assume a POSIX shell at /bin/sh.
public sealed class ProcessSandboxTests : IDisposable
{
    private readonly string _workingRoot = Path.Combine(Path.GetTempPath(), $"orkis-sandbox-tests-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(_workingRoot))
        {
            Directory.Delete(_workingRoot, recursive: true);
        }
    }

    private ProcessSandbox CreateSandbox(Action<ProcessSandboxOptions>? configure = null)
    {
        var options = new ProcessSandboxOptions { WorkingRoot = _workingRoot };
        configure?.Invoke(options);
        return new ProcessSandbox(Options.Create(options));
    }

    private static SandboxExecutionRequest Shell(string script, TimeSpan? timeout = null) =>
        new()
        {
            Executable = "/bin/sh",
            Arguments = ["-c", script],
            Timeout = timeout,
        };

    [Fact]
    public void ReportsStandardIsolationLevel() => Assert.Equal(SandboxLevel.Standard, CreateSandbox().Level);

    [Fact]
    public async Task CapturesStandardOutputAndExitCode()
    {
        var result = await CreateSandbox().ExecuteAsync(Shell("echo hello"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.StandardOutput.Trim());
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task CapturesStandardErrorAndNonZeroExitCode()
    {
        var result = await CreateSandbox().ExecuteAsync(Shell("echo oops 1>&2; exit 3"));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("oops", result.StandardError.Trim());
    }

    [Fact]
    public async Task KillsProcessOnTimeout()
    {
        var result = await CreateSandbox().ExecuteAsync(Shell("sleep 30", timeout: TimeSpan.FromMilliseconds(250)));

        Assert.True(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task StripsHostEnvironment()
    {
        Environment.SetEnvironmentVariable("ORKIS_SECRET", "leaked");
        try
        {
            var result = await CreateSandbox().ExecuteAsync(Shell("printenv ORKIS_SECRET"));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("", result.StandardOutput.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORKIS_SECRET", null);
        }
    }

    [Fact]
    public async Task CapsOutputWhileLettingTheProcessFinish()
    {
        var result = await CreateSandbox(o => o.MaxOutputLength = 1024)
            .ExecuteAsync(Shell("for i in $(seq 1 10000); do echo 0123456789; done"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1024, result.StandardOutput.Length);
    }

    [Fact]
    public async Task RunsInScratchDirectoryAndCleansItUp()
    {
        var result = await CreateSandbox().ExecuteAsync(Shell("pwd"));

        var scratch = result.StandardOutput.Trim();
        Assert.StartsWith(_workingRoot, scratch, StringComparison.Ordinal);
        Assert.False(Directory.Exists(scratch));
    }

    [Fact]
    public async Task RejectsWorkingDirectoryThatEscapesTheSandbox()
    {
        var request = new SandboxExecutionRequest
        {
            Executable = "/bin/sh",
            Arguments = ["-c", "pwd"],
            WorkingDirectory = "../outside",
        };

        await Assert.ThrowsAsync<ArgumentException>(() => CreateSandbox().ExecuteAsync(request));
    }

    [Fact]
    public async Task CreatesRequestedRelativeWorkingDirectory()
    {
        var request = new SandboxExecutionRequest
        {
            Executable = "/bin/sh",
            Arguments = ["-c", "pwd"],
            WorkingDirectory = "nested/dir",
        };

        var result = await CreateSandbox().ExecuteAsync(request);

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith(Path.Combine("nested", "dir"), result.StandardOutput.Trim(), StringComparison.Ordinal);
    }
}
