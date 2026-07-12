using System.Text.Json;
using Orkis.Agents;
using Orkis.Artifacts;
using Orkis.Client;
using Orkis.Runs;
using Orkis.Sandboxing;
using Orkis.Supervision;

namespace Orkis.Daemon;

/// <summary>
/// The daemon's HTTP surface. Commands are plain JSON request/response; the run-event
/// stream is Server-Sent Events whose <c>data:</c> payload is the same self-describing
/// JSON the durable log stores, with <c>id:</c> carrying <see cref="RunEvent.Sequence"/>
/// so <c>Last-Event-ID</c> reconnection replays exactly the missed events.
/// </summary>
internal static class DaemonEndpoints
{
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapOrkisDaemon(this WebApplication app)
    {
        app.MapGet("/v1/healthz", static () => Results.Ok(new { status = "ok" }));

        app.MapPost(
            "/v1/runs",
            static (StartRunRequest body, ISupervisorResolver supervisors, RunExecutor executor) =>
            {
                if (string.IsNullOrWhiteSpace(body.Prompt))
                {
                    return Results.BadRequest(new { error = "prompt is required." });
                }

                var supervisorKey = body.SupervisorKey ?? "queue";
                try
                {
                    supervisors.Resolve(supervisorKey);
                }
                catch (InvalidOperationException)
                {
                    return Results.BadRequest(new { error = $"Unknown supervisor key '{supervisorKey}'." });
                }

                var request = new AgentRunRequest
                {
                    Prompt = body.Prompt,
                    // Parity with the CLI host: a run without its own system prompt
                    // still gets the guardrail against invented tool results.
                    SystemPrompt =
                        body.SystemPrompt
                        ?? "You are an Orkis agent. Use the available tools to fulfil the request, "
                            + "then summarize what happened.\n\n"
                            + SystemPromptFragments.ConfabulationGuardrail,
                    SupervisorKey = supervisorKey,
                    Budget = new RunBudget { MaxTokens = body.MaxTokens, MaxToolCalls = body.MaxToolCalls },
                };
                executor.TryStart(request);
                return Results.Accepted($"/v1/runs/{request.RunId}", new RunAcceptedResponse { RunId = request.RunId });
            }
        );

        app.MapGet(
            "/v1/runs",
            static async (RunRegistry registry, RunExecutor executor, CancellationToken cancellationToken) =>
            {
                var summaries = await registry.ListAsync(cancellationToken);
                var runs = summaries.Select(summary => ToResponse(summary, executor)).ToList();

                // An accepted run has no checkpoint until its first step completes;
                // surface it anyway so a start is immediately observable.
                var known = summaries.Select(static s => s.RunId).ToHashSet(StringComparer.Ordinal);
                runs.AddRange(
                    executor.ActiveRunIds.Where(id => !known.Contains(id)).Select(id => ActiveOnlyResponse(id))
                );
                return Results.Ok(runs);
            }
        );

        app.MapGet(
            "/v1/runs/{runId}",
            static async (
                string runId,
                RunRegistry registry,
                RunExecutor executor,
                CancellationToken cancellationToken
            ) =>
            {
                var summary = await registry.GetAsync(runId, cancellationToken);
                if (summary is not null)
                {
                    return Results.Ok(ToResponse(summary, executor));
                }

                return executor.IsActive(runId) ? Results.Ok(ActiveOnlyResponse(runId)) : Results.NotFound();
            }
        );

        app.MapPost(
            "/v1/runs/{runId}/resume",
            static async (
                string runId,
                RunRegistry registry,
                RunExecutor executor,
                CancellationToken cancellationToken
            ) =>
            {
                var summary = await registry.GetAsync(runId, cancellationToken);
                if (summary is null)
                {
                    return Results.NotFound();
                }

                if (summary.Status is not (RunStatus.Running or RunStatus.AwaitingSupervision))
                {
                    return Results.Conflict(
                        new { error = $"Run '{runId}' has already ended with status {summary.Status}." }
                    );
                }

                if (!executor.TryResume(runId))
                {
                    return Results.Conflict(new { error = $"Run '{runId}' is already executing." });
                }

                return Results.Accepted($"/v1/runs/{runId}", new RunAcceptedResponse { RunId = runId });
            }
        );

        app.MapGet("/v1/runs/{runId}/events", StreamEventsAsync);

        app.MapGet("/v1/events", StreamAllEventsAsync);

        app.MapGet(
            "/v1/approvals",
            static async (string? runId, IApprovalInbox inbox, CancellationToken cancellationToken) =>
            {
                var pending = await inbox.ListPendingAsync(cancellationToken);
                return Results.Ok(
                    pending
                        .Where(approval => runId is null || approval.RunId == runId)
                        .Select(static approval => new ApprovalResponse
                        {
                            RunId = approval.RunId,
                            CallId = approval.Call.Id,
                            ToolName = approval.Call.ToolName,
                            Risk = approval.Tool.Risk,
                            RequestedAt = approval.RequestedAt,
                            Arguments = approval.Call.Arguments,
                        })
                        .ToList()
                );
            }
        );

        app.MapPost(
            "/v1/approvals/{runId}/{callId}",
            static async (
                string runId,
                string callId,
                DecideApprovalRequest body,
                IApprovalInbox inbox,
                CancellationToken cancellationToken
            ) =>
            {
                SupervisionDecision decision;
                if (string.Equals(body.Verdict, "approve", StringComparison.OrdinalIgnoreCase))
                {
                    SandboxLevel? sandboxLevel = null;
                    if (body.SandboxLevel is not null)
                    {
                        if (!Enum.TryParse<SandboxLevel>(body.SandboxLevel, ignoreCase: true, out var parsedLevel))
                        {
                            return Results.BadRequest(new { error = $"Unknown sandbox level '{body.SandboxLevel}'." });
                        }

                        sandboxLevel = parsedLevel;
                    }

                    NetworkMode? network = null;
                    if (body.Network is not null)
                    {
                        if (!Enum.TryParse<NetworkMode>(body.Network, ignoreCase: true, out var parsedNetwork))
                        {
                            return Results.BadRequest(new { error = $"Unknown network mode '{body.Network}'." });
                        }

                        network = parsedNetwork;
                    }

                    decision = SupervisionDecision.Approve(sandboxLevel, network);
                }
                else if (string.Equals(body.Verdict, "deny", StringComparison.OrdinalIgnoreCase))
                {
                    decision = SupervisionDecision.Deny(body.Reason ?? "The operator denied this action.");
                }
                else
                {
                    return Results.BadRequest(new { error = "verdict must be 'approve' or 'deny'." });
                }

                try
                {
                    await inbox.DecideAsync(runId, callId, decision, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }

                return Results.NoContent();
            }
        );

        app.MapGet(
            "/v1/artifacts",
            static async (IArtifactStore artifacts, CancellationToken cancellationToken) =>
                Results.Ok(await artifacts.ListAsync(cancellationToken))
        );
    }

    private static RunResponse ToResponse(RunSummary summary, RunExecutor executor) =>
        new()
        {
            RunId = summary.RunId,
            Status = summary.Status,
            Active = executor.IsActive(summary.RunId),
            SupervisorKey = summary.SupervisorKey,
            InputTokens = summary.InputTokens,
            OutputTokens = summary.OutputTokens,
            Cost = summary.Cost,
            ToolCalls = summary.ToolCalls,
            UpdatedAt = summary.UpdatedAt,
            LastError = executor.FailureFor(summary.RunId),
        };

    private static RunResponse ActiveOnlyResponse(string runId) =>
        new()
        {
            RunId = runId,
            Status = RunStatus.Running,
            Active = true,
        };

    /// <summary>
    /// Streams the run's events as SSE: recorded history after <c>Last-Event-ID</c>
    /// (or <c>?after=</c>) first, then — with <c>?follow=true</c> — live events until
    /// the run completes or the client disconnects. The subscription starts before the
    /// history read, so nothing written between the two is missed; duplicates are
    /// dropped by sequence.
    /// </summary>
    private static async Task StreamEventsAsync(
        string runId,
        bool? follow,
        HttpContext context,
        RunEventBroker broker,
        IRunEventLog eventLog,
        RunRegistry registry,
        RunExecutor executor
    )
    {
        var cancellationToken = context.RequestAborted;
        var after = -1L;
        if (
            context.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
            && long.TryParse(lastEventId, out var fromHeader)
        )
        {
            after = fromHeader;
        }
        else if (long.TryParse(context.Request.Query["after"], out var fromQuery))
        {
            after = fromQuery;
        }

        using var subscription = broker.Subscribe(runId);
        var history = await eventLog.ReadAsync(runId, after, cancellationToken);
        if (
            history.Count == 0
            && !executor.IsActive(runId)
            && await registry.GetAsync(runId, cancellationToken) is null
        )
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";

        try
        {
            var lastSequence = after;
            var ended = false;
            foreach (var runEvent in history)
            {
                await WriteEventAsync(context.Response, runEvent, cancellationToken);
                lastSequence = runEvent.Sequence;
                ended |= runEvent is RunCompletedEvent;
            }

            if (follow is not true || ended)
            {
                return;
            }

            await foreach (var runEvent in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                if (runEvent.Sequence <= lastSequence)
                {
                    continue;
                }

                await WriteEventAsync(context.Response, runEvent, cancellationToken);
                lastSequence = runEvent.Sequence;
                if (runEvent is RunCompletedEvent)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away; SSE reconnection picks up from Last-Event-ID.
        }
    }

    /// <summary>
    /// Streams every run's live events as one SSE stream — the dashboard feed. No
    /// history and no <c>id:</c> fields: sequences are per run, so global resume
    /// semantics would be a lie. Clients bootstrap from the <c>/v1/runs</c> and
    /// <c>/v1/approvals</c> snapshots and apply live events on top, re-snapshotting
    /// after a reconnect.
    /// </summary>
    private static async Task StreamAllEventsAsync(HttpContext context, RunEventBroker broker)
    {
        var cancellationToken = context.RequestAborted;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";

        using var subscription = broker.SubscribeAll();
        await context.Response.Body.FlushAsync(cancellationToken);
        try
        {
            await foreach (var runEvent in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await WriteEventAsync(context.Response, runEvent, cancellationToken, includeId: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away.
        }
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        RunEvent runEvent,
        CancellationToken cancellationToken,
        bool includeId = true
    )
    {
        // One line per event: RunEvent's polymorphic JSON has no raw newlines, so a
        // single data: field carries it unaltered — the same bytes as the JSONL log.
        var json = JsonSerializer.Serialize(runEvent, WireJsonOptions);
        var frame = includeId ? $"id: {runEvent.Sequence}\ndata: {json}\n\n" : $"data: {json}\n\n";
        await response.WriteAsync(frame, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
