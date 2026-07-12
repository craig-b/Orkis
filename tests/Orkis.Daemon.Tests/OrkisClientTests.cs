using Orkis.Agents;
using Orkis.Client;
using Orkis.Runs;

namespace Orkis.Daemon.Tests;

/// <summary>
/// Drives the daemon through the typed <see cref="OrkisClient"/> — the same path the
/// CLI (and later clients) take — over a real Unix socket.
/// </summary>
public sealed class OrkisClientTests(DaemonFixture fixture) : IClassFixture<DaemonFixture>, IDisposable
{
    private readonly OrkisClient _client = new(fixture.SocketPath);

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task ReportsHealthy()
    {
        Assert.True(await _client.IsHealthyAsync());
    }

    [Fact]
    public async Task DrivesTheFullQueueFlowTyped()
    {
        var accepted = await _client.StartRunAsync(new StartRunRequest { Prompt = "Run the greeting command." });

        var paused = await WaitForRunAsync(
            accepted.RunId,
            static r => !r.Active && r.Status == RunStatus.AwaitingSupervision
        );
        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);

        var approval = Assert.Single(await _client.ListApprovalsAsync(accepted.RunId));
        Assert.Equal("run_shell_command", approval.ToolName);

        await _client.DecideApprovalAsync(
            approval.RunId,
            approval.CallId,
            new DecideApprovalRequest { Verdict = "approve" }
        );
        await _client.ResumeRunAsync(accepted.RunId);

        var completed = await WaitForRunAsync(accepted.RunId, static r => !r.Active && r.Status == RunStatus.Completed);
        Assert.Equal(RunStatus.Completed, completed.Status);

        var events = new List<RunEvent>();
        await foreach (var runEvent in _client.StreamEventsAsync(accepted.RunId))
        {
            events.Add(runEvent);
        }

        Assert.IsType<RunStartedEvent>(events[0]);
        Assert.IsType<RunCompletedEvent>(events[^1]);
        Assert.DoesNotContain(events, static e => e is UnknownRunEvent);

        Assert.Contains(await _client.ListRunsAsync(), r => r.RunId == accepted.RunId);
    }

    [Fact]
    public async Task ChatsAwaitTheUserAndContinueAcrossTurns()
    {
        var accepted = await _client.StartRunAsync(
            new StartRunRequest
            {
                Prompt = "Run the greeting command.",
                SupervisorKey = "yolo",
                Chat = true,
            }
        );

        var afterFirstTurn = await WaitForRunAsync(
            accepted.RunId,
            static r => !r.Active && r.Status == RunStatus.AwaitingUser
        );
        Assert.Equal(RunStatus.AwaitingUser, afterFirstTurn.Status);

        var transcript = await _client.GetTranscriptAsync(accepted.RunId);
        Assert.NotNull(transcript);
        var firstTurnMessages = transcript.Count;
        Assert.Contains(transcript, static m => m.Role == "user");
        Assert.Contains(transcript, static m => m.Role == "assistant");

        await _client.ContinueRunAsync(accepted.RunId, "And again, please.");
        await WaitForRunAsync(accepted.RunId, static r => !r.Active && r.Status == RunStatus.AwaitingUser);

        transcript = await _client.GetTranscriptAsync(accepted.RunId);
        Assert.NotNull(transcript);
        Assert.True(transcript.Count > firstTurnMessages);
        Assert.Contains(transcript, static m => m.Text == "And again, please.");
    }

    [Fact]
    public async Task MessagesToANonChatRunConflict()
    {
        var accepted = await _client.StartRunAsync(
            new StartRunRequest { Prompt = "Run the greeting command.", SupervisorKey = "yolo" }
        );
        await WaitForRunAsync(accepted.RunId, static r => !r.Active && r.Status == RunStatus.Completed);

        var ex = await Assert.ThrowsAsync<OrkisApiException>(() => _client.ContinueRunAsync(accepted.RunId, "more"));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, ex.StatusCode);
    }

    [Fact]
    public async Task CapabilitiesEnumerateWhatTheDaemonOffers()
    {
        var capabilities = await _client.GetCapabilitiesAsync();

        Assert.Contains("queue", capabilities.Supervisors);
        Assert.Contains("yolo", capabilities.Supervisors);
        Assert.Equal("queue", capabilities.DefaultSupervisor);
        Assert.Contains("alt", capabilities.Models);
        Assert.Equal("process", capabilities.Sandbox);
        Assert.False(capabilities.Memory);
        Assert.Contains("run_shell_command", capabilities.Tools);
    }

    [Fact]
    public async Task ModelKeyRoutesTheRunAndIsRecordedInItsEvents()
    {
        var accepted = await _client.StartRunAsync(
            new StartRunRequest
            {
                Prompt = "Run the greeting command.",
                SupervisorKey = "yolo",
                Model = "alt",
            }
        );
        await WaitForRunAsync(accepted.RunId, static r => !r.Active && r.Status == RunStatus.Completed);

        var events = new List<RunEvent>();
        await foreach (var runEvent in _client.StreamEventsAsync(accepted.RunId))
        {
            events.Add(runEvent);
        }

        var started = Assert.IsType<RunStartedEvent>(events[0]);
        Assert.Equal("alt", started.ModelKey);
    }

    [Fact]
    public async Task UnknownModelKeyIsRejectedUpFront()
    {
        var ex = await Assert.ThrowsAsync<OrkisApiException>(() =>
            _client.StartRunAsync(new StartRunRequest { Prompt = "hi", Model = "nonsense" })
        );

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("model", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GlobalStreamMultiplexesEveryRun()
    {
        // Live-only stream: subscribe before starting the runs.
        using var streamCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var seenRuns = new HashSet<string>(StringComparer.Ordinal);
        var completedRuns = new HashSet<string>(StringComparer.Ordinal);

        var streaming = Task.Run(async () =>
        {
            await foreach (var runEvent in _client.StreamAllEventsAsync(streamCancellation.Token))
            {
                seenRuns.Add(runEvent.RunId);
                if (runEvent is RunCompletedEvent)
                {
                    completedRuns.Add(runEvent.RunId);
                }

                if (completedRuns.Count >= 2)
                {
                    return;
                }
            }
        });

        // Give the SSE connection a moment to establish before producing events.
        await Task.Delay(250);
        var first = await _client.StartRunAsync(
            new StartRunRequest { Prompt = "Run the greeting command.", SupervisorKey = "yolo" }
        );
        var second = await _client.StartRunAsync(
            new StartRunRequest { Prompt = "Run the greeting command.", SupervisorKey = "yolo" }
        );

        await streaming;
        Assert.Contains(first.RunId, seenRuns);
        Assert.Contains(second.RunId, seenRuns);
        Assert.Contains(first.RunId, completedRuns);
        Assert.Contains(second.RunId, completedRuns);
    }

    [Fact]
    public async Task SurfacesDaemonErrorsAsTypedExceptions()
    {
        Assert.Null(await _client.GetRunAsync("no-such-run"));

        var resume = await Assert.ThrowsAsync<OrkisApiException>(() => _client.ResumeRunAsync("no-such-run"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resume.StatusCode);

        var decide = await Assert.ThrowsAsync<OrkisApiException>(() =>
            _client.DecideApprovalAsync(
                "no-such-run",
                "no-such-call",
                new DecideApprovalRequest { Verdict = "approve" }
            )
        );
        Assert.Equal(System.Net.HttpStatusCode.Conflict, decide.StatusCode);
    }

    private async Task<RunResponse> WaitForRunAsync(string runId, Func<RunResponse, bool> done)
    {
        var stopAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        RunResponse? last = null;
        while (DateTime.UtcNow < stopAt)
        {
            last = await _client.GetRunAsync(runId);
            if (last is not null && done(last))
            {
                return last;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Run '{runId}' did not reach the expected state in time; last seen: {last}");
        return null!; // unreachable
    }
}
