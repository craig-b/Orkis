using System.Diagnostics;
using System.Text;

namespace Orkis.Sandboxing;

/// <summary>Shared child-process plumbing: timeout kill and capped output capture.</summary>
internal static class SandboxProcess
{
    public static async Task<SandboxExecutionResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        int maxOutputLength,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // The sandboxed command gets an empty, closed stdin rather than the host
        // terminal, so a command that reads stdin fails fast instead of hanging.
        process.StandardInput.Close();

        // Readers use the caller's token, not the timeout token: after a timeout kill the
        // pipes reach end-of-file, and we still want the output captured up to that point.
        var stdoutTask = ReadCappedAsync(process.StandardOutput, maxOutputLength, cancellationToken);
        var stderrTask = ReadCappedAsync(process.StandardError, maxOutputLength, cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
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
}

/// <summary>Shared scratch-directory handling for sandboxes.</summary>
internal static class SandboxScratch
{
    /// <summary>
    /// Picks the scratch root for an execution: the workload's persistent workspace
    /// directory when a key is given (shared by every execution with that key, kept
    /// afterwards), or a fresh throwaway directory when not.
    /// </summary>
    public static string Locate(string workingRoot, string? workspaceKey) =>
        workspaceKey is { } key
            ? Path.Combine(workingRoot, "workspaces", SafePathNames.For(key))
            : Path.Combine(workingRoot, Guid.CreateVersion7().ToString("n"));

    /// <summary>Resolves the requested working directory inside the scratch root, rejecting escapes.</summary>
    public static string Resolve(string scratchDirectory, string? requested)
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

    /// <summary>
    /// Resolves a file path inside a workload's persistent workspace, rejecting escapes.
    /// </summary>
    public static string ResolveWorkspaceFile(string workingRoot, string workspaceKey, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceKey);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        return Resolve(Path.Combine(workingRoot, "workspaces", SafePathNames.For(workspaceKey)), relativePath);
    }

    /// <summary>Opens a workspace file for reading, or null when it does not exist.</summary>
    public static Task<Stream?> OpenWorkspaceFileAsync(string workingRoot, string workspaceKey, string relativePath)
    {
        var path = ResolveWorkspaceFile(workingRoot, workspaceKey, relativePath);
        return Task.FromResult<Stream?>(
            File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous)
                : null
        );
    }

    /// <summary>Writes a workspace file, creating parent directories and replacing any existing file.</summary>
    public static async Task WriteWorkspaceFileAsync(
        string workingRoot,
        string workspaceKey,
        string relativePath,
        Stream content,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = ResolveWorkspaceFile(workingRoot, workspaceKey, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    public static void TryDelete(string path)
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
