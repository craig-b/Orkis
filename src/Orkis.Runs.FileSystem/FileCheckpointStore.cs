using System.Globalization;
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

        // Same-sequence rewrites win over the previous file, matching the
        // in-memory store's replace-on-equal-sequence behavior.
        await AtomicJsonFile.WriteAsync(finalPath, checkpoint, options: null, cancellationToken).ConfigureAwait(false);
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

        return await AtomicJsonFile
            .ReadAsync<RunCheckpoint>(latestPath, options: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private string GetRunDirectory(string runId) => Path.Combine(_rootPath, SafePathNames.For(runId));
}
