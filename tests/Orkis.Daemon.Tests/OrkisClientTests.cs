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
