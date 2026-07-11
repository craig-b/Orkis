using System.Diagnostics;
using System.Text;

namespace Orkis.Sandboxing;

/// <summary>Runs a host process with timeout kill and capped output capture.</summary>
internal static class FirecrackerProcess
{
    public sealed record Outcome(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

    public static async Task<Outcome> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        int maxOutputLength,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

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

        return new Outcome(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            timedOut
        );
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
            // Already exited.
        }
    }
}
