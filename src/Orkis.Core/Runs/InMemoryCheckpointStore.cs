using System.Collections.Concurrent;

namespace Orkis.Runs;

/// <summary>
/// Keeps the latest checkpoint per run in process memory. Suitable for development
/// and testing; checkpoints do not survive a process restart.
/// </summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly ConcurrentDictionary<string, RunCheckpoint> _latestByRun = new();

    /// <inheritdoc />
    public Task SaveAsync(RunCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        _latestByRun.AddOrUpdate(
            checkpoint.RunId,
            checkpoint,
            (_, existing) => checkpoint.Sequence >= existing.Sequence ? checkpoint : existing
        );
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RunCheckpoint?> LoadLatestAsync(string runId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_latestByRun.TryGetValue(runId, out var checkpoint) ? checkpoint : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<RunCheckpoint>> ListLatestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RunCheckpoint>>([.. _latestByRun.Values]);
}
