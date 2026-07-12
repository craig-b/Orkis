using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Orkis.Sandboxing;

/// <summary>
/// Executes commands as child processes with a stripped environment, a confined
/// per-execution scratch directory, output caps, and timeout kill.
/// </summary>
/// <remarks>
/// This provides <see cref="SandboxLevel.Standard"/> isolation: it constrains
/// well-behaved and accidentally-misbehaving commands, but it is not a security
/// boundary against deliberately malicious code — the child process runs with the
/// host process's OS privileges. Use <see cref="BubblewrapSandbox"/> or another
/// <see cref="SandboxLevel.Strict"/> sandbox for untrusted code.
/// </remarks>
public sealed class ProcessSandbox : ISandbox, IWorkspaceFileAccess
{
    private readonly ProcessSandboxOptions _options;
    private readonly TimeProvider _timeProvider;

    public ProcessSandbox(IOptions<ProcessSandboxOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public SandboxLevel Level => SandboxLevel.Standard;

    /// <inheritdoc />
    public Task<Stream?> ReadWorkspaceFileAsync(
        string workspaceKey,
        string relativePath,
        CancellationToken cancellationToken = default
    ) => SandboxScratch.OpenWorkspaceFileAsync(_options.WorkingRoot, workspaceKey, relativePath);

    /// <inheritdoc />
    public Task WriteWorkspaceFileAsync(
        string workspaceKey,
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default
    ) =>
        SandboxScratch.WriteWorkspaceFileAsync(
            _options.WorkingRoot,
            workspaceKey,
            relativePath,
            content,
            cancellationToken
        );

    /// <inheritdoc />
    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var scratchDirectory = SandboxScratch.Locate(_options.WorkingRoot, request.WorkspaceKey);
        var workingDirectory = SandboxScratch.Resolve(scratchDirectory, request.WorkingDirectory);
        Directory.CreateDirectory(workingDirectory);

        var startInfo = new ProcessStartInfo { FileName = request.Executable, WorkingDirectory = workingDirectory };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var name in _options.EnvironmentAllowlist)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                startInfo.Environment[name] = value;
            }
        }

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
            if (request.WorkspaceKey is null)
            {
                SandboxScratch.TryDelete(scratchDirectory);
            }
        }
    }
}
