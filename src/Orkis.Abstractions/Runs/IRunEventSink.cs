namespace Orkis.Runs;

/// <summary>
/// Receives run events as they happen. The runner awaits the sink, so a durable sink
/// makes the event log trustworthy; fan-out sinks (live UI subscribers) should
/// isolate their subscribers' failures rather than fail the run.
/// </summary>
public interface IRunEventSink
{
    /// <summary>Records one event.</summary>
    ValueTask WriteAsync(RunEvent runEvent, CancellationToken cancellationToken = default);
}

/// <summary>Reads back a run's recorded event history.</summary>
public interface IRunEventLog
{
    /// <summary>
    /// Returns the run's events with <see cref="RunEvent.Sequence"/> greater than
    /// <paramref name="afterSequence"/>, in order.
    /// </summary>
    Task<IReadOnlyList<RunEvent>> ReadAsync(
        string runId,
        long afterSequence = -1,
        CancellationToken cancellationToken = default
    );
}
