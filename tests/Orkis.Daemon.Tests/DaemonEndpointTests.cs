using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orkis.Agents;
using Orkis.Client;

namespace Orkis.Daemon.Tests;

public sealed class DaemonEndpointTests(DaemonFixture fixture) : IClassFixture<DaemonFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task HealthzResponds()
    {
        var response = await Client.GetAsync(new Uri("/v1/healthz", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task YoloRunCompletesAndReplaysItsEventStream()
    {
        var runId = await StartRunAsync(supervisorKey: "yolo");

        var run = await WaitForRunAsync(runId, static r => !r.Active && r.Status != RunStatus.Running);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Null(run.LastError);
        Assert.True(run.ToolCalls > 0);

        var events = await ReadEventsAsync(runId);
        Assert.Equal("run_started", TypeOf(events[0]));
        Assert.Equal("run_completed", TypeOf(events[^1]));
        Assert.Contains(events, static e => TypeOf(e) == "tool_call_completed");
        var sequences = events.Select(static e => e.RootElement.GetProperty("sequence").GetInt64()).ToList();
        Assert.Equal([.. Enumerable.Range(0, events.Count).Select(static i => (long)i)], sequences);

        var listed = await Client.GetFromJsonAsync<List<RunResponse>>(
            new Uri("/v1/runs", UriKind.Relative),
            DaemonFixture.JsonOptions
        );
        Assert.NotNull(listed);
        Assert.Contains(listed, r => r.RunId == runId);
    }

    [Fact]
    public async Task EventStreamReplaysOnlyAfterLastEventId()
    {
        var runId = await StartRunAsync(supervisorKey: "yolo");
        await WaitForRunAsync(runId, static r => !r.Active && r.Status != RunStatus.Running);

        var all = await ReadEventsAsync(runId);
        var tail = await ReadEventsAsync(runId, after: 1);

        Assert.Equal(all.Count - 2, tail.Count);
        Assert.Equal(2, tail[0].RootElement.GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task FollowStreamsLiveUntilRunCompletes()
    {
        var runId = await StartRunAsync(supervisorKey: "yolo");

        // Following joins the recorded history to the live tail, so connecting at any
        // point during (or after) the run yields the same complete stream.
        var events = await ReadEventsAsync(runId, follow: true);

        Assert.Equal("run_started", TypeOf(events[0]));
        Assert.Equal("run_completed", TypeOf(events[^1]));
    }

    [Fact]
    public async Task QueueRunPausesThenApprovalAutoContinuesIt()
    {
        var runId = await StartRunAsync();

        var paused = await WaitForRunAsync(runId, static r => !r.Active && r.Status == RunStatus.AwaitingSupervision);
        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);

        var approvals = await Client.GetFromJsonAsync<List<ApprovalResponse>>(
            new Uri($"/v1/approvals?runId={runId}", UriKind.Relative),
            DaemonFixture.JsonOptions
        );
        Assert.NotNull(approvals);
        var approval = Assert.Single(approvals);
        Assert.Equal("run_shell_command", approval.ToolName);

        // Deciding is the whole interaction — the daemon resumes the run itself.
        var decide = await Client.PostAsJsonAsync(
            new Uri($"/v1/approvals/{runId}/{approval.CallId}", UriKind.Relative),
            new DecideApprovalRequest { Verdict = "approve" },
            DaemonFixture.JsonOptions
        );
        Assert.Equal(HttpStatusCode.NoContent, decide.StatusCode);

        var completed = await WaitForRunAsync(runId, static r => !r.Active && r.Status == RunStatus.Completed);
        Assert.Equal(RunStatus.Completed, completed.Status);

        var events = await ReadEventsAsync(runId);
        Assert.Contains(events, static e => TypeOf(e) == "run_paused");
        Assert.Contains(events, static e => TypeOf(e) == "run_resumed");
        Assert.Contains(events, static e => TypeOf(e) == "supervision_decided");
    }

    [Fact]
    public async Task DeniedApprovalStillLetsTheRunFinish()
    {
        var runId = await StartRunAsync();
        await WaitForRunAsync(runId, static r => !r.Active && r.Status == RunStatus.AwaitingSupervision);

        var approvals = await Client.GetFromJsonAsync<List<ApprovalResponse>>(
            new Uri($"/v1/approvals?runId={runId}", UriKind.Relative),
            DaemonFixture.JsonOptions
        );
        var approval = Assert.Single(approvals!);

        var decide = await Client.PostAsJsonAsync(
            new Uri($"/v1/approvals/{runId}/{approval.CallId}", UriKind.Relative),
            new DecideApprovalRequest { Verdict = "deny", Reason = "not in tests" },
            DaemonFixture.JsonOptions
        );
        Assert.Equal(HttpStatusCode.NoContent, decide.StatusCode);

        // The daemon continues the run after a denial too — the agent sees the refusal.
        var completed = await WaitForRunAsync(runId, static r => !r.Active && r.Status == RunStatus.Completed);
        Assert.Equal(RunStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task StartRejectsUnknownSupervisorAndBlankPrompt()
    {
        var unknown = await Client.PostAsJsonAsync(
            new Uri("/v1/runs", UriKind.Relative),
            new StartRunRequest { Prompt = "hi", SupervisorKey = "nonsense" },
            DaemonFixture.JsonOptions
        );
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var blank = await Client.PostAsJsonAsync(
            new Uri("/v1/runs", UriKind.Relative),
            new StartRunRequest { Prompt = " " },
            DaemonFixture.JsonOptions
        );
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [Fact]
    public async Task UnknownRunsReturnNotFound()
    {
        var get = await Client.GetAsync(new Uri("/v1/runs/no-such-run", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var resume = await Client.PostAsync(new Uri("/v1/runs/no-such-run/resume", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.NotFound, resume.StatusCode);

        var events = await Client.GetAsync(new Uri("/v1/runs/no-such-run/events", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, events.StatusCode);
    }

    [Fact]
    public async Task ResumingACompletedRunConflicts()
    {
        var runId = await StartRunAsync(supervisorKey: "yolo");
        await WaitForRunAsync(runId, static r => !r.Active && r.Status == RunStatus.Completed);

        var resume = await Client.PostAsync(new Uri($"/v1/runs/{runId}/resume", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode);
    }

    [Fact]
    public async Task DecidingAnUnknownApprovalConflicts()
    {
        var decide = await Client.PostAsJsonAsync(
            new Uri("/v1/approvals/no-such-run/no-such-call", UriKind.Relative),
            new DecideApprovalRequest { Verdict = "approve" },
            DaemonFixture.JsonOptions
        );

        Assert.Equal(HttpStatusCode.Conflict, decide.StatusCode);
    }

    private async Task<string> StartRunAsync(string? supervisorKey = null)
    {
        var response = await Client.PostAsJsonAsync(
            new Uri("/v1/runs", UriKind.Relative),
            new StartRunRequest { Prompt = "Run the greeting command.", SupervisorKey = supervisorKey },
            DaemonFixture.JsonOptions
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var accepted = await response.Content.ReadFromJsonAsync<RunAcceptedResponse>(DaemonFixture.JsonOptions);
        Assert.NotNull(accepted);
        return accepted.RunId;
    }

    private async Task<RunResponse> WaitForRunAsync(string runId, Func<RunResponse, bool> done)
    {
        var stopAt = DateTime.UtcNow + Deadline;
        RunResponse? last = null;
        while (DateTime.UtcNow < stopAt)
        {
            var response = await Client.GetAsync(new Uri($"/v1/runs/{runId}", UriKind.Relative));
            if (response.StatusCode == HttpStatusCode.OK)
            {
                last = await response.Content.ReadFromJsonAsync<RunResponse>(DaemonFixture.JsonOptions);
                if (last is not null && done(last))
                {
                    return last;
                }
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Run '{runId}' did not reach the expected state in time; last seen: {last}");
        return null!; // unreachable
    }

    private async Task<List<JsonDocument>> ReadEventsAsync(string runId, long? after = null, bool follow = false)
    {
        var uri = new Uri($"/v1/runs/{runId}/events?follow={(follow ? "true" : "false")}", UriKind.Relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (after is not null)
        {
            request.Headers.Add("Last-Event-ID", after.Value.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = new List<JsonDocument>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                events.Add(JsonDocument.Parse(line["data: ".Length..]));
            }
        }

        Assert.NotEmpty(events);
        return events;
    }

    private static string TypeOf(JsonDocument runEvent) => runEvent.RootElement.GetProperty("$type").GetString()!;
}
