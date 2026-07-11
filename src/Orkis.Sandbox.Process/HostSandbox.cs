using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Orkis.Sandboxing;

/// <summary>Configuration for <see cref="HostSandbox"/>.</summary>
public sealed class HostSandboxOptions
{
    /// <summary>Directory under which each execution gets its own scratch working directory.</summary>
    public string WorkingRoot { get; set; } = Path.Combine(Path.GetTempPath(), "orkis-host");

    /// <summary>Timeout applied when a request does not specify one.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum characters captured per output stream.</summary>
    public int MaxOutputLength { get; set; } = 64 * 1024;
}

/// <summary>
/// Runs commands directly on the host with the full host environment, network, and
/// filesystem — <see cref="SandboxLevel.None"/>.
/// </summary>
/// <remarks>
/// This is not a security boundary in any sense: the command runs with the host
/// process's privileges and can see and change everything the host can. Register it
/// only when unshielded execution is a deliberate choice — for trusted commands, or
/// so a supervisor can approve host execution explicitly and raise isolation when it
/// does not. Commands still run in a scratch working directory and are subject to a
/// timeout and output caps, but those are conveniences, not containment.
/// </remarks>
public sealed class HostSandbox : ISandbox
{
    private readonly HostSandboxOptions _options;
    private readonly TimeProvider _timeProvider;

    public HostSandbox(IOptions<HostSandboxOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public SandboxLevel Level => SandboxLevel.None;

    /// <inheritdoc />
    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var scratchDirectory = Path.Combine(_options.WorkingRoot, Guid.CreateVersion7().ToString("n"));
        var workingDirectory = SandboxScratch.Resolve(scratchDirectory, request.WorkingDirectory);
        Directory.CreateDirectory(workingDirectory);

        var startInfo = new ProcessStartInfo { FileName = request.Executable, WorkingDirectory = workingDirectory };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // No environment manipulation: the command inherits the host's full environment.
        try
        {
            return await SandboxProcess
                .RunAsync(
                    startInfo,
                    request.Timeout ?? _options.DefaultTimeout,
                    _options.MaxOutputLength,
                    _timeProvider,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        finally
        {
            SandboxScratch.TryDelete(scratchDirectory);
        }
    }
}
