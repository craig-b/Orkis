using Orkis.Agents;
using Orkis.Runs;
using Orkis.Supervision;

namespace Orkis.Core.Tests;

public sealed class ChatTests : IDisposable
{
    private readonly FakeChatClient _chatClient = new();
    private readonly InMemoryCheckpointStore _checkpoints = new();
    private readonly FakeRunEventSink _events = new();
    private readonly RunRegistry _registry;
    private readonly AgentRunner _runner;

    public ChatTests()
    {
        _registry = new RunRegistry(_checkpoints);
        _runner = new AgentRunner(
            _chatClient,
            [],
            new FakeSupervisorResolver(new AutoApproveSupervisor()),
            _checkpoints,
            TimeProvider.System,
            eventSink: _events
        );
    }

    public void Dispose() => _chatClient.Dispose();

    private static AgentRunRequest Chat(string prompt) =>
        new()
        {
            RunId = "chat-1",
            Prompt = prompt,
            Conversational = true,
        };

    [Fact]
    public async Task ATurnEndsAwaitingTheUserNotTerminal()
    {
        _chatClient.Enqueue(TestResponses.Text("hello!"));

        var result = await _runner.StartAsync(Chat("hi"));

        Assert.Equal(RunStatus.AwaitingUser, result.Status);
        Assert.Equal("hello!", result.FinalText);
        var turn = Assert.IsType<TurnCompletedEvent>(_events.Events[^1]);
        Assert.Equal("hello!", turn.FinalTextPreview);
    }

    [Fact]
    public async Task ContinuingGrowsTheTranscriptAndAccumulatesTheBudget()
    {
        _chatClient.Enqueue(TestResponses.Text("first reply"));
        _chatClient.Enqueue(TestResponses.Text("second reply"));

        await _runner.StartAsync(Chat("first message"));
        var second = await _runner.ContinueAsync("chat-1", "second message");

        Assert.Equal(RunStatus.AwaitingUser, second.Status);
        Assert.Equal("second reply", second.FinalText);
        // Usage accumulates across turns: two model calls of 10/5 each.
        Assert.Equal(20, second.Usage.InputTokens);
        Assert.Equal(10, second.Usage.OutputTokens);

        // The second model call saw the whole conversation.
        var lastRequest = _chatClient.Requests[^1];
        Assert.Contains(lastRequest, static m => m.Text == "first message");
        Assert.Contains(lastRequest, static m => m.Text == "first reply");
        Assert.Contains(lastRequest, static m => m.Text == "second message");

        var transcript = await _registry.GetTranscriptAsync("chat-1");
        Assert.NotNull(transcript);
        Assert.Equal(
            ["first message", "first reply", "second message", "second reply"],
            transcript.Select(static m => m.Text).ToArray()
        );
        Assert.Equal(["user", "assistant", "user", "assistant"], transcript.Select(static m => m.Role).ToArray());
    }

    [Fact]
    public async Task TheChatBudgetSpansTurns()
    {
        _chatClient.Enqueue(TestResponses.Text("first reply"));
        _chatClient.Enqueue(TestResponses.Text("second reply"));

        await _runner.StartAsync(Chat("first") with { Budget = new RunBudget { MaxTokens = 25 } });
        var second = await _runner.ContinueAsync("chat-1", "second");
        Assert.Equal(RunStatus.AwaitingUser, second.Status);

        // 30 tokens spent across two turns: the chat-level cap gates the next turn
        // before it makes a model call (budgets stop future work, not the last reply).
        var third = await _runner.ContinueAsync("chat-1", "third");
        Assert.Equal(RunStatus.BudgetExceeded, third.Status);
    }

    [Fact]
    public async Task ContinuingANonChatRunFails()
    {
        _chatClient.Enqueue(TestResponses.Text("done"));
        await _runner.StartAsync(new AgentRunRequest { RunId = "run-1", Prompt = "one shot" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.ContinueAsync("run-1", "more"));

        Assert.Contains("already ended", ex.Message);
    }

    [Fact]
    public async Task ResumingAChatAwaitingTheUserPointsAtContinue()
    {
        _chatClient.Enqueue(TestResponses.Text("hello!"));
        await _runner.StartAsync(Chat("hi"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.ResumeAsync("chat-1"));

        Assert.Contains("continue", ex.Message);
    }

    [Fact]
    public async Task ContinuationsEmitRunContinuedEvents()
    {
        _chatClient.Enqueue(TestResponses.Text("first reply"));
        _chatClient.Enqueue(TestResponses.Text("second reply"));

        await _runner.StartAsync(Chat("first"));
        await _runner.ContinueAsync("chat-1", "second");

        var continued = Assert.Single(_events.Events.OfType<RunContinuedEvent>());
        Assert.Equal("second", continued.Message);

        // Sequences stay strictly increasing across turns.
        var sequences = _events.Events.Select(static e => e.Sequence).ToList();
        Assert.Equal(sequences.OrderBy(static s => s).ToList(), sequences);
        Assert.Equal(sequences.Count, sequences.Distinct().Count());
    }
}
