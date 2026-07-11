using System.Text.Json;

namespace Orkis.Runs;

/// <summary>
/// A durable snapshot of a run's complete state, written after every step so the
/// run can resume after a crash, restart, or pause awaiting supervision.
/// </summary>
public sealed record RunCheckpoint
{
    /// <summary>The run this checkpoint belongs to.</summary>
    public required string RunId { get; init; }

    /// <summary>Monotonically increasing position of this checkpoint within the run.</summary>
    public required long Sequence { get; init; }

    /// <summary>The run's serialized state as a JSON document.</summary>
    public required JsonElement State { get; init; }

    /// <summary>When the checkpoint was written.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
