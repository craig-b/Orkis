using System.Text.Json;
using Orkis.Agents;
using Orkis.Runs;
using Orkis.Sandboxing;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Core.Tests;

public sealed class QueueSupervisorTests
{
    private readonly InMemoryApprovalInbox _queue = new();
    private readonly QueueSupervisor _supervisor;

    public QueueSupervisorTests() => _supervisor = new QueueSupervisor(_queue, TimeProvider.System);

    private static ProposedAction Action(string runId = "run-1", string callId = "call-1") =>
        new()
        {
            RunId = runId,
            Call = new ToolCall
            {
                Id = callId,
                ToolName = "fake_tool",
                Arguments = JsonDocument.Parse("{}").RootElement,
            },
            Tool = new FakeTool().Descriptor,
        };

    [Fact]
    public async Task FirstReviewDefersAndQueuesTheAction()
    {
        var decision = await _supervisor.ReviewAsync(Action());

        Assert.Equal(SupervisionVerdict.Pending, decision.Verdict);
        var pending = Assert.Single(await _queue.ListPendingAsync());
        Assert.Equal("run-1", pending.RunId);
        Assert.Equal("call-1", pending.Call.Id);
        Assert.Equal("fake_tool", pending.Call.ToolName);
    }

    [Fact]
    public async Task ReReviewWithoutADecisionStillDefersWithoutDuplicating()
    {
        await _supervisor.ReviewAsync(Action());
        var decision = await _supervisor.ReviewAsync(Action());

        Assert.Equal(SupervisionVerdict.Pending, decision.Verdict);
        Assert.Single(await _queue.ListPendingAsync());
    }

    [Fact]
    public async Task RecordedApprovalIsReturnedWithItsSandboxLevel()
    {
        await _supervisor.ReviewAsync(Action());
        await _queue.DecideAsync("run-1", "call-1", SupervisionDecision.Approve(SandboxLevel.Standard));

        var decision = await _supervisor.ReviewAsync(Action());

        Assert.Equal(SupervisionVerdict.Approved, decision.Verdict);
        Assert.Equal(SandboxLevel.Standard, decision.RequiredSandboxLevel);
        Assert.Empty(await _queue.ListPendingAsync());
    }

    [Fact]
    public async Task RecordedDenialIsReturnedWithItsReason()
    {
        await _supervisor.ReviewAsync(Action());
        await _queue.DecideAsync("run-1", "call-1", SupervisionDecision.Deny("too risky"));

        var decision = await _supervisor.ReviewAsync(Action());

        Assert.Equal(SupervisionVerdict.Denied, decision.Verdict);
        Assert.Equal("too risky", decision.Reason);
    }

    [Fact]
    public async Task DecidingAnUnknownApprovalThrows()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _queue.DecideAsync("run-1", "call-1", SupervisionDecision.Approve())
        );
    }

    [Fact]
    public async Task DecidingTwiceThrows()
    {
        await _supervisor.ReviewAsync(Action());
        await _queue.DecideAsync("run-1", "call-1", SupervisionDecision.Approve());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _queue.DecideAsync("run-1", "call-1", SupervisionDecision.Deny("changed my mind"))
        );
    }

    [Fact]
    public async Task APendingVerdictCannotBeRecordedAsADecision()
    {
        await _supervisor.ReviewAsync(Action());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _queue.DecideAsync("run-1", "call-1", SupervisionDecision.Defer())
        );
    }

    [Fact]
    public async Task RunPausesUntilQueueDecisionThenResumeCompletes()
    {
        using var chatClient = new FakeChatClient();
        var tool = new FakeTool(risk: ToolRisk.Destructive);
        var checkpoints = new InMemoryCheckpointStore();
        var runner = new AgentRunner(
            chatClient,
            [tool],
            new FakeSupervisorResolver(_supervisor),
            checkpoints,
            TimeProvider.System
        );

        chatClient.Enqueue(TestResponses.ToolCall("call-1", "fake_tool"));
        chatClient.Enqueue(TestResponses.Text("done after approval"));

        var paused = await runner.StartAsync(new AgentRunRequest { RunId = "run-1", Prompt = "use the tool" });

        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);
        Assert.Equal(0, tool.Invocations);
        var pending = Assert.Single(await _queue.ListPendingAsync());
        Assert.Equal("run-1", pending.RunId);

        await _queue.DecideAsync("run-1", "call-1", SupervisionDecision.Approve());
        var resumed = await runner.ResumeAsync("run-1");

        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal("done after approval", resumed.FinalText);
        Assert.Equal(1, tool.Invocations);
        Assert.Empty(await _queue.ListPendingAsync());
    }
}
