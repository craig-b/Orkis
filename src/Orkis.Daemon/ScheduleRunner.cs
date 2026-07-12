using System.Collections.Concurrent;
using Cronos;
using Orkis.Agents;
using Orkis.Runs;
using Orkis.Scheduling;

namespace Orkis.Daemon;

/// <summary>
/// Fires schedules on their cron cadence. Each firing is a fresh run; continuity is
/// shared storage and an optional handoff note. Missed firings are skipped (the next
/// future occurrence is computed, not caught up), and a firing still running when the
/// next is due is skipped with a log line — overlapping firings sharing a workspace
/// and handoff would be incoherent.
/// </summary>
internal sealed partial class ScheduleRunner : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private readonly IScheduleStore _schedules;
    private readonly RunnerFactory _runners;
    private readonly RunRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<ScheduleRunner> _log;

    // When the runner first saw each schedule, so a never-fired schedule computes its
    // next occurrence from now rather than the epoch.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _baseline = new(StringComparer.Ordinal);

    public ScheduleRunner(
        IScheduleStore schedules,
        RunnerFactory runners,
        RunRegistry registry,
        TimeProvider time,
        ILogger<ScheduleRunner> log
    )
    {
        _schedules = schedules;
        _runners = runners;
        _registry = registry;
        _time = time;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _time);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTickFailed(ex);
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        foreach (var schedule in await _schedules.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!schedule.Enabled || !IsDue(schedule, now))
            {
                continue;
            }

            await FireAsync(schedule, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsDue(Schedule schedule, DateTimeOffset now)
    {
        if (!TryParse(schedule, out var cron))
        {
            return false;
        }

        var from = schedule.LastFiredAt ?? _baseline.GetOrAdd(schedule.Id, now);
        var next = cron.GetNextOccurrence(from.UtcDateTime, TimeZoneInfo.Utc);
        return next is not null && next.Value <= now.UtcDateTime;
    }

    private bool TryParse(Schedule schedule, out CronExpression cron)
    {
        try
        {
            // Six-field expressions carry seconds; five-field do not.
            var format =
                schedule.Cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 6
                    ? CronFormat.IncludeSeconds
                    : CronFormat.Standard;
            cron = CronExpression.Parse(schedule.Cron, format);
            return true;
        }
        catch (CronFormatException ex)
        {
            LogInvalidCron(ex, schedule.Id, schedule.Cron);
            cron = null!;
            return false;
        }
    }

    private async Task FireAsync(Schedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Skip if the previous firing has not finished — no overlapping firings on a
        // shared workspace. Recorded as a firing (LastFiredAt advances) so the skipped
        // slot does not immediately re-trigger.
        if (schedule.LastRunId is { } lastRunId)
        {
            var last = await _registry.GetAsync(lastRunId, cancellationToken).ConfigureAwait(false);
            if (last is not null && last.Status is RunStatus.Running or RunStatus.AwaitingSupervision)
            {
                LogSkippedOverlap(schedule.Id);
                await _schedules
                    .SaveAsync(schedule with { LastFiredAt = now }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        var shared = schedule.Continuity != ScheduleContinuity.Fresh;
        var scope = shared ? $"sched-{schedule.Id}" : "default";
        var prompt = schedule.Prompt;
        if (schedule.Continuity == ScheduleContinuity.SharedStorageWithHandoff && schedule.Handoff is { Length: > 0 })
        {
            prompt =
                $"Note from your previous run ({schedule.LastFiredAt:u}):\n{schedule.Handoff}\n\n"
                + $"---\n{schedule.Prompt}";
        }

        var request = new AgentRunRequest
        {
            Prompt = prompt,
            SupervisorKey = schedule.SupervisorKey,
            ModelKey = schedule.ModelKey,
            ToolNames = schedule.ToolNames,
            MemoryScope = shared ? scope : Orkis.Memory.MemoryScopes.Global,
            Origin = $"schedule:{schedule.Id}",
            Budget = new RunBudget { MaxTokens = schedule.MaxTokens },
        };

        LogFiring(schedule.Id, request.RunId);
        await _schedules
            .SaveAsync(schedule with { LastFiredAt = now, LastRunId = request.RunId }, cancellationToken)
            .ConfigureAwait(false);

        // Fire and forget: the run is independent of the daemon's request lifetime,
        // and its handoff (if any) is captured from its completion event by
        // ScheduleHandoffService — so a firing that parks for approval and resumes
        // later still contributes its note.
        _ = RunAsync(schedule.Id, scope, request);
    }

    private async Task RunAsync(string scheduleId, string workspaceKey, AgentRunRequest request)
    {
        try
        {
            var runner = _runners.CreateForScope(workspaceKey, request.MemoryScope);
            await runner.StartAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRunFailed(ex, scheduleId);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Schedule tick failed.")]
    private partial void LogTickFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule '{id}' has an invalid cron expression '{cron}'.")]
    private partial void LogInvalidCron(Exception ex, string id, string cron);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Schedule '{id}' skipped a firing: previous run still active."
    )]
    private partial void LogSkippedOverlap(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule '{id}' firing run {runId}.")]
    private partial void LogFiring(string id, string runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled run for '{scheduleId}' failed.")]
    private partial void LogRunFailed(Exception ex, string scheduleId);
}
