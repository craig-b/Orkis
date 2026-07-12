using Orkis.Runs;
using Orkis.Scheduling;

namespace Orkis.Daemon;

/// <summary>
/// Captures a schedule's handoff note when one of its firings completes — driven by
/// the run-event stream rather than the firing call, so it works whether the run ran
/// straight through (yolo) or parked for approval and resumed later. The note is the
/// firing's final assistant message, seeding the next firing's prompt.
/// </summary>
internal sealed partial class ScheduleHandoffService : BackgroundService
{
    private readonly RunEventBroker _broker;
    private readonly RunRegistry _registry;
    private readonly IScheduleStore _schedules;
    private readonly ILogger<ScheduleHandoffService> _log;

    public ScheduleHandoffService(
        RunEventBroker broker,
        RunRegistry registry,
        IScheduleStore schedules,
        ILogger<ScheduleHandoffService> log
    )
    {
        _broker = broker;
        _registry = registry;
        _schedules = schedules;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _broker.SubscribeAll();
        try
        {
            await foreach (var runEvent in subscription.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                if (runEvent is RunCompletedEvent { Status: "Completed" } completed)
                {
                    await TryCaptureAsync(completed.RunId, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task TryCaptureAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            var summary = await _registry.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            if (summary?.Origin is not { } origin || !origin.StartsWith("schedule:", StringComparison.Ordinal))
            {
                return;
            }

            var scheduleId = origin["schedule:".Length..];
            var schedule = await _schedules.GetAsync(scheduleId, cancellationToken).ConfigureAwait(false);
            if (schedule is null || schedule.Continuity != ScheduleContinuity.SharedStorageWithHandoff)
            {
                return;
            }

            // Only this firing's note matters — a later firing overwrites it. Guard
            // against an out-of-order completion for a superseded run.
            if (schedule.LastRunId is not null && schedule.LastRunId != runId)
            {
                return;
            }

            var transcript = await _registry.GetTranscriptAsync(runId, cancellationToken).ConfigureAwait(false);
            var note = transcript?.LastOrDefault(message => message.Role == "assistant")?.Text;
            if (string.IsNullOrWhiteSpace(note))
            {
                return;
            }

            await _schedules.SaveAsync(schedule with { Handoff = note }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCaptureFailed(ex, runId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to capture handoff for scheduled run {runId}.")]
    private partial void LogCaptureFailed(Exception ex, string runId);
}
