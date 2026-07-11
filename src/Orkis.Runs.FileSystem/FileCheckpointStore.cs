using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Orkis.Runs;

/// <summary>
/// Stores checkpoints as JSON files under a root directory — one subdirectory per run,
/// one file per checkpoint sequence — so runs survive a process restart. Writes land in
/// a temporary file that is moved into place, so a crash mid-write never leaves a
/// truncated checkpoint where the latest one should be.
/// </summary>
public sealed class FileCheckpointStore : ICheckpointStore
{
    /// <summary>Sanitized run-id prefix length kept for directory readability.</summary>
    private const int RunIdPrefixLength = 48;

    private readonly string _rootPath;

    public FileCheckpointStore(IOptions<FileCheckpointStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rootPath = Path.GetFullPath(options.Value.RootPath);
    }

    /// <inheritdoc />
    public async Task SaveAsync(RunCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var runDirectory = GetRunDirectory(checkpoint.RunId);
        Directory.CreateDirectory(runDirectory);

        var finalPath = Path.Combine(
            runDirectory,
            checkpoint.Sequence.ToString("D19", CultureInfo.InvariantCulture) + ".json"
        );
        var tempPath = finalPath + "." + Guid.NewGuid().ToString("n") + ".tmp";

        try
        {
            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous
            );
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer
                    .SerializeAsync(stream, checkpoint, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Same-sequence rewrites win over the previous file, matching the
            // in-memory store's replace-on-equal-sequence behavior.
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<RunCheckpoint?> LoadLatestAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var runDirectory = GetRunDirectory(runId);
        if (!Directory.Exists(runDirectory))
        {
            return null;
        }

        string? latestPath = null;
        var latestSequence = long.MinValue;
        foreach (var path in Directory.EnumerateFiles(runDirectory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (
                long.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var sequence)
                && sequence >= latestSequence
            )
            {
                latestSequence = sequence;
                latestPath = path;
            }
        }

        if (latestPath is null)
        {
            return null;
        }

        var stream = new FileStream(
            latestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous
        );
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer
                .DeserializeAsync<RunCheckpoint>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Maps a run id to a directory that is safe regardless of what the id contains: a
    /// readable sanitized prefix plus a hash of the full id, so ids with path separators
    /// or other special characters can neither escape the root nor collide after
    /// sanitization.
    /// </summary>
    private string GetRunDirectory(string runId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))[..16];

        Span<char> prefix = stackalloc char[Math.Min(runId.Length, RunIdPrefixLength)];
        for (var i = 0; i < prefix.Length; i++)
        {
            var c = runId[i];
            prefix[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }

        return Path.Combine(_rootPath, $"{prefix}-{hash}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
