using Orkis.Agents;
using Orkis.Runs;
using Orkis.Supervision;

namespace Orkis.Core.Tests;

public sealed class RunEventTests : IDisposable
{
    private readonly FakeChatClient _chatClient = new();
    private readonly FakeTool _tool = new();
    private readonly ScriptedSupervisor _supervisor = new();
    private readonly InMemoryCheckpointStore _checkpointStore = new();
    private readonly FakeRunEventSink _sink = new();

    public void Dispose() => _chatClient.Dispose();

    private AgentRunner CreateRunner() =>
        new(
            _chatClient,
            [_tool],
            new FakeSupervisorResolver(_supervisor),
            _checkpointStore,
            TimeProvider.System,
            eventSink: _sink
        );

    [Fact]
    public async Task AFullRunEmitsTheCompleteOrderedHistory()
    {
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "fake_tool"));
        _chatClient.Enqueue(TestResponses.Text("done"));

        await CreateRunner().StartAsync(new AgentRunRequest { RunId = "run-1", Prompt = "go" });

        Assert.Equal(
            [
                typeof(RunStartedEvent),
                typeof(ModelCallCompletedEvent),
                typeof(ToolCallProposedEvent),
                typeof(SupervisionDecidedEvent),
                typeof(ToolCallCompletedEvent),
                typeof(ModelCallCompletedEvent),
                typeof(RunCompletedEvent),
            ],
            _sink.Events.Select(e => e.GetType())
        );

        Assert.All(_sink.Events, e => Assert.Equal("run-1", e.RunId));
        Assert.Equal(
            Enumerable.Range(0, _sink.Events.Count).Select(i => (long)i),
            _sink.Events.Select(e => e.Sequence)
        );

        var decided = _sink.Events.OfType<SupervisionDecidedEvent>().Single();
        Assert.Equal("Approved", decided.Verdict);
        Assert.Equal("fake_tool", decided.ToolName);

        var completed = _sink.Events.OfType<RunCompletedEvent>().Single();
        Assert.Equal("Completed", completed.Status);
        Assert.Contains("done", completed.FinalTextPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PauseAndResumeContinueTheSequenceWithoutGapsOrRepeats()
    {
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "fake_tool"));
        _supervisor.Enqueue(SupervisionDecision.Defer());

        var runner = CreateRunner();
        var paused = await runner.StartAsync(new AgentRunRequest { RunId = "run-1", Prompt = "go" });
        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);
        Assert.IsType<RunPausedEvent>(_sink.Events[^1]);

        _chatClient.Enqueue(TestResponses.Text("done"));
        await runner.ResumeAsync("run-1");

        Assert.Equal(
            [
                typeof(RunStartedEvent),
                typeof(ModelCallCompletedEvent),
                typeof(ToolCallProposedEvent),
                typeof(SupervisionDecidedEvent), // Pending
                typeof(RunPausedEvent),
                typeof(RunResumedEvent),
                typeof(SupervisionDecidedEvent), // Approved on re-review
                typeof(ToolCallCompletedEvent),
                typeof(ModelCallCompletedEvent),
                typeof(RunCompletedEvent),
            ],
            _sink.Events.Select(e => e.GetType())
        );

        // The event sequence survives the checkpoint round trip: strictly increasing,
        // no restarts after resume.
        Assert.Equal(
            Enumerable.Range(0, _sink.Events.Count).Select(i => (long)i),
            _sink.Events.Select(e => e.Sequence)
        );
        Assert.Equal("Pending", _sink.Events.OfType<SupervisionDecidedEvent>().First().Verdict);
    }
}
