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
    public async Task WorkspaceFilesPersistAcrossExecutionsAndInstances()
    {
        var request = Shell("echo persisted > note.txt") with { WorkspaceKey = "chat-1" };
        await CreateSandbox().ExecuteAsync(request);

        var read = await CreateSandbox().ExecuteAsync(Shell("cat note.txt") with { WorkspaceKey = "chat-1" });

        Assert.Equal(0, read.ExitCode);
        Assert.Equal("persisted", read.StandardOutput.Trim());
    }

    [Fact]
    public async Task DistinctWorkspaceKeysAreIsolated()
    {
        var sandbox = CreateSandbox();
        await sandbox.ExecuteAsync(Shell("echo secret > note.txt") with { WorkspaceKey = "chat-1" });

        var other = await sandbox.ExecuteAsync(Shell("cat note.txt") with { WorkspaceKey = "chat-2" });

        Assert.NotEqual(0, other.ExitCode);
    }

    [Fact]
    public async Task WithoutAWorkspaceKeyTheScratchIsDeleted()
    {
        var sandbox = CreateSandbox();
        await sandbox.ExecuteAsync(Shell("echo gone > note.txt"));

        var again = await sandbox.ExecuteAsync(Shell("cat note.txt"));

        Assert.NotEqual(0, again.ExitCode);
        Assert.DoesNotContain(
            Directory.GetDirectories(_workingRoot),
            d => !d.EndsWith("workspaces", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task HostileWorkspaceKeysStayInsideTheWorkingRoot()
    {
        var request = Shell("echo contained > note.txt") with { WorkspaceKey = "../../etc/escape" };
        var result = await CreateSandbox().ExecuteAsync(request);

        Assert.Equal(0, result.ExitCode);
        var fullRoot = Path.GetFullPath(_workingRoot);
        var file = Assert.Single(Directory.EnumerateFiles(fullRoot, "note.txt", SearchOption.AllDirectories));
        Assert.StartsWith(fullRoot + Path.DirectorySeparatorChar, file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceFileAccessReadsWhatCommandsWrote()
    {
        var sandbox = CreateSandbox();
        await sandbox.ExecuteAsync(
            Shell("mkdir -p out; printf inner-content > out/report.txt") with
            {
                WorkspaceKey = "chat-1",
            }
        );

        var stream = await sandbox.ReadWorkspaceFileAsync("chat-1", "out/report.txt");

        Assert.NotNull(stream);
        await using (stream)
        {
            using var reader = new StreamReader(stream);
            Assert.Equal("inner-content", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task WorkspaceFileAccessWritesWhatCommandsCanRead()
    {
        var sandbox = CreateSandbox();
        using var content = new MemoryStream("staged-content"u8.ToArray());
        await sandbox.WriteWorkspaceFileAsync("chat-1", "input/data.txt", content);

        var result = await sandbox.ExecuteAsync(Shell("cat input/data.txt") with { WorkspaceKey = "chat-1" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("staged-content", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task WorkspaceFileAccessReturnsNullForMissingFiles()
    {
        Assert.Null(await CreateSandbox().ReadWorkspaceFileAsync("chat-1", "nope.txt"));
    }

    [Fact]
    public async Task WorkspaceFileAccessRejectsEscapingPaths()
    {
        var sandbox = CreateSandbox();

        await Assert.ThrowsAsync<ArgumentException>(() => sandbox.ReadWorkspaceFileAsync("chat-1", "../outside.txt"));
        using var content = new MemoryStream([1]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sandbox.WriteWorkspaceFileAsync("chat-1", "/etc/passwd", content)
        );
    }

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
