namespace Orkis.Runs;

/// <summary>Durable storage for run checkpoints.</summary>
public interface ICheckpointStore
{
    /// <summary>Persists a checkpoint. Sequence numbers within a run must be unique.</summary>
    Task SaveAsync(RunCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Returns the latest checkpoint for the run, or <see langword="null"/> if none exists.</summary>
    Task<RunCheckpoint?> LoadLatestAsync(string runId, CancellationToken cancellationToken = default);
}
