using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Orkis.Agents;
using Orkis.Runs;
using Orkis.Supervision;

namespace Orkis.Core.Tests;

public sealed class CompactingContextPolicyTests : IDisposable
{
    private readonly FakeChatClient _summarizer = new();

    public void Dispose() => _summarizer.Dispose();

    private CompactingContextPolicy CreatePolicy(
        long triggerTokens,
        int keepRecent = 2,
        int maxAgedToolChars = 40,
        bool withSummarizer = true
    ) =>
        new(
            withSummarizer ? _summarizer : null,
            Options.Create(
                new CompactingContextPolicyOptions
                {
                    TriggerTokens = triggerTokens,
                    KeepRecentMessages = keepRecent,
                    MaxAgedToolResultChars = maxAgedToolChars,
                }
            )
        );

    private static ChatMessage AssistantCall(string callId)
    {
        FunctionCallContent call = new(callId, "some_tool", new Dictionary<string, object?>());
        return new ChatMessage(ChatRole.Assistant, [call]);
    }

    private static ChatMessage ToolResult(string callId, string text)
    {
        FunctionResultContent result = new(callId, text);
        return new ChatMessage(ChatRole.Tool, [result]);
    }

    private static ContextRequest Request(
        IReadOnlyList<ChatMessage> transcript,
        Dictionary<string, string>? cache = null
    ) => new() { Transcript = transcript, Cache = cache ?? [] };

    private static List<ChatMessage> BusyTranscript() =>
        [
            new(ChatRole.System, "You are the agent."),
            new(ChatRole.User, "Do the big task."),
            new(ChatRole.Assistant, "Working on step one now, with some commentary."),
            AssistantCall("call-1"),
            ToolResult("call-1", new string('x', 500)),
            new(ChatRole.Assistant, "Step one done; moving to step two with more words."),
            new(ChatRole.Assistant, "Interim reasoning that goes on for a while and a while."),
            new(ChatRole.Assistant, "More narration to give the summarizer something real."),
            new(ChatRole.User, "Keep going."),
            new(ChatRole.Assistant, "Recent tail message that must stay verbatim."),
        ];

    [Fact]
    public async Task UnderTheTriggerThePassThroughIsExact()
    {
        var transcript = BusyTranscript();

        var view = await CreatePolicy(triggerTokens: 1_000_000).ComposeAsync(Request(transcript));

        Assert.Same(transcript, view.Messages);
        Assert.Empty(view.CacheUpdates);
        Assert.Empty(_summarizer.Requests);
    }

    [Fact]
    public async Task AgedToolOutputsAreStubbedButRecentOnesKeptVerbatim()
    {
        List<ChatMessage> transcript =
        [
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "task"),
            AssistantCall("old"),
            ToolResult("old", new string('a', 500)),
            AssistantCall("new"),
            ToolResult("new", new string('b', 500)),
        ];

        var view = await CreatePolicy(triggerTokens: 10, withSummarizer: false).ComposeAsync(Request(transcript));

        var oldResult = (FunctionResultContent)view.Messages[3].Contents[0];
        var newResult = (FunctionResultContent)view.Messages[5].Contents[0];
        Assert.Contains("elided", oldResult.Result?.ToString(), StringComparison.Ordinal);
        Assert.Equal(new string('b', 500), newResult.Result?.ToString());
    }

    [Fact]
    public async Task OverTheTriggerOldSpansAreSummarizedAndCached()
    {
        _summarizer.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "THE-SUMMARY")));
        var transcript = BusyTranscript();

        var view = await CreatePolicy(triggerTokens: 10).ComposeAsync(Request(transcript));

        // system + first user survive; the middle is one framed summary; the tail stays.
        Assert.Equal(ChatRole.System, view.Messages[0].Role);
        Assert.Equal("Do the big task.", view.Messages[1].Text);
        Assert.Contains("context compaction", view.Messages[2].Text, StringComparison.Ordinal);
        Assert.Contains("THE-SUMMARY", view.Messages[2].Text, StringComparison.Ordinal);
        Assert.Equal("Recent tail message that must stay verbatim.", view.Messages[^1].Text);
        Assert.True(view.Messages.Count < transcript.Count);

        var update = Assert.Single(view.CacheUpdates);
        Assert.StartsWith("summary:", update.Key, StringComparison.Ordinal);
        Assert.Equal("THE-SUMMARY", update.Value);
    }

    [Fact]
    public async Task CachedSummariesAreNotRecomputed()
    {
        _summarizer.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "THE-SUMMARY")));
        var transcript = BusyTranscript();
        var policy = CreatePolicy(triggerTokens: 10);

        var first = await policy.ComposeAsync(Request(transcript));
        var cache = first.CacheUpdates.ToDictionary(StringComparer.Ordinal);
        var second = await policy.ComposeAsync(Request(transcript, cache));

        Assert.Single(_summarizer.Requests);
        Assert.Empty(second.CacheUpdates);
        Assert.Contains("THE-SUMMARY", second.Messages[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCutNeverOrphansToolResults()
    {
        _summarizer.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "S")));
        List<ChatMessage> transcript =
        [
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "task with plenty of words to blow the tiny budget"),
            new(ChatRole.Assistant, "one narration message with lots and lots of words"),
            new(ChatRole.Assistant, "two narration message with lots and lots of words"),
            new(ChatRole.Assistant, "three narration message with lots and lots of words"),
            new(ChatRole.Assistant, "four narration message with lots and lots of words"),
            AssistantCall("c1"),
            ToolResult("c1", "the tool answer"),
            new(ChatRole.Assistant, "closing"),
        ];

        // keepRecent = 2 puts the protected boundary between AssistantCall and its
        // ToolResult; the span must shrink rather than split the pair.
        var view = await CreatePolicy(triggerTokens: 10, keepRecent: 2).ComposeAsync(Request(transcript));

        var summaryIndex = view
            .Messages.Select((m, i) => (m, i))
            .First(pair => pair.m.Text.Contains("context compaction", StringComparison.Ordinal))
            .i;
        Assert.NotEqual(ChatRole.Tool, view.Messages[summaryIndex + 1].Role);
        Assert.Contains(view.Messages, static m => m.Contents.Any(static c => c is FunctionCallContent));
    }
}

public sealed class ContextPolicyRunnerTests : IDisposable
{
    private sealed class RecordingContextPolicy : IContextPolicy
    {
        public List<ContextRequest> Requests { get; } = [];

        public Dictionary<string, string> EmitCacheUpdates { get; } = [];

        public Task<ContextView> ComposeAsync(ContextRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(
                new ContextView
                {
                    Messages = [new ChatMessage(ChatRole.User, "composed-view")],
                    CacheUpdates = EmitCacheUpdates,
                }
            );
        }
    }

    private readonly FakeChatClient _chatClient = new();
    private readonly ScriptedSupervisor _supervisor = new();
    private readonly InMemoryCheckpointStore _checkpointStore = new();
    private readonly RecordingContextPolicy _policy = new();

    public void Dispose() => _chatClient.Dispose();

    private AgentRunner CreateRunner() =>
        new(
            _chatClient,
            [new FakeTool()],
            new FakeSupervisorResolver(_supervisor),
            _checkpointStore,
            TimeProvider.System,
            contextPolicy: _policy
        );

    [Fact]
    public async Task TheModelSeesTheComposedViewWhileStateKeepsTheFullTranscript()
    {
        _chatClient.Enqueue(TestResponses.Text("done"));

        var result = await CreateRunner()
            .StartAsync(new AgentRunRequest { Prompt = "the real prompt", SystemPrompt = "sys" });

        Assert.Equal("composed-view", Assert.Single(_chatClient.Requests).Single().Text);
        // The policy received the true transcript, not its own output.
        Assert.Contains(Assert.Single(_policy.Requests).Transcript, static m => m.Text == "the real prompt");
        Assert.Equal("done", result.FinalText);
    }

    [Fact]
    public async Task CacheUpdatesSurviveCheckpointAndResume()
    {
        _policy.EmitCacheUpdates["summary:1-4"] = "cached-summary";
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "fake_tool"));
        _supervisor.Enqueue(SupervisionDecision.Defer());

        var runner = CreateRunner();
        var paused = await runner.StartAsync(new AgentRunRequest { RunId = "run-1", Prompt = "go" });
        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);

        _chatClient.Enqueue(TestResponses.Text("done"));
        await runner.ResumeAsync("run-1");

        // The resumed segment's compose call sees the cache written before the pause.
        var resumedRequest = _policy.Requests[^1];
        Assert.Equal("cached-summary", resumedRequest.Cache["summary:1-4"]);
    }
}
