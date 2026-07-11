using Microsoft.Extensions.Options;
using Orkis.Sandboxing;

namespace Orkis.Sandbox.Process.Tests;

// Assumes a POSIX shell at /bin/sh.
public sealed class HostSandboxTests : IDisposable
{
    private readonly string _workingRoot = Path.Combine(Path.GetTempPath(), $"orkis-host-tests-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(_workingRoot))
        {
            Directory.Delete(_workingRoot, recursive: true);
        }
    }

    private HostSandbox CreateSandbox() => new(Options.Create(new HostSandboxOptions { WorkingRoot = _workingRoot }));

    private static SandboxExecutionRequest Shell(string script) =>
        new() { Executable = "/bin/sh", Arguments = ["-c", script] };

    [Fact]
    public void ReportsNoIsolationLevel() => Assert.Equal(SandboxLevel.None, CreateSandbox().Level);

    [Fact]
    public async Task RunsCommandsAndCapturesOutput()
    {
        var result = await CreateSandbox().ExecuteAsync(Shell("echo hello"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task InheritsHostEnvironment()
    {
        // Contrast with ProcessSandbox, which strips this. Host execution keeps it.
        Environment.SetEnvironmentVariable("ORKIS_HOST_VISIBLE", "yes");
        try
        {
            var result = await CreateSandbox().ExecuteAsync(Shell("printenv ORKIS_HOST_VISIBLE"));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("yes", result.StandardOutput.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORKIS_HOST_VISIBLE", null);
        }
    }
}
