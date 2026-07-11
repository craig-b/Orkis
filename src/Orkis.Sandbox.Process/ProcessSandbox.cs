using System.Diagnostics;
using System.Text;
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
/// host process's OS privileges. Use a <see cref="SandboxLevel.Strict"/> sandbox
/// (container or micro-VM based) for untrusted code.
/// </remarks>
public sealed class ProcessSandbox : ISandbox
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
    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var scratchDirectory = Path.Combine(_options.WorkingRoot, Guid.CreateVersion7().ToString("n"));
        var workingDirectory = ResolveWorkingDirectory(scratchDirectory, request.WorkingDirectory);
        Directory.CreateDirectory(workingDirectory);

        try
        {
            return await RunProcessAsync(request, workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(scratchDirectory);
        }
    }

    private static string ResolveWorkingDirectory(string scratchDirectory, string? requested)
    {
        if (requested is null)
        {
            return scratchDirectory;
        }

        if (Path.IsPathRooted(requested))
        {
            throw new ArgumentException(
                "The working directory must be a relative path inside the sandbox.",
                nameof(requested)
            );
        }

        var resolved = Path.GetFullPath(Path.Combine(scratchDirectory, requested));
        if (!resolved.StartsWith(scratchDirectory, StringComparison.Ordinal))
        {
            throw new ArgumentException("The working directory escapes the sandbox.", nameof(requested));
        }

        return resolved;
    }

    private async Task<SandboxExecutionResult> RunProcessAsync(
        SandboxExecutionRequest request,
        string workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
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

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Readers use the caller's token, not the timeout token: after a timeout kill the
        // pipes reach end-of-file, and we still want the output captured up to that point.
        var stdoutTask = ReadCappedAsync(process.StandardOutput, _options.MaxOutputLength, cancellationToken);
        var stderrTask = ReadCappedAsync(process.StandardError, _options.MaxOutputLength, cancellationToken);

        var timeout = request.Timeout ?? _options.DefaultTimeout;
        using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token
        );

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new SandboxExecutionResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdoutTask.ConfigureAwait(false),
            StandardError = await stderrTask.ConfigureAwait(false),
            TimedOut = timedOut,
        };
    }

    private static async Task<string> ReadCappedAsync(
        StreamReader reader,
        int maxLength,
        CancellationToken cancellationToken
    )
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var remaining = maxLength - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            // Past the cap we keep reading without storing, so the child never
            // blocks writing to a full pipe.
        }

        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a straggling grandchild process may still hold files.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
