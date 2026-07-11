using Microsoft.Extensions.AI;
using Orkis.Agents;
using Orkis.Runs;
using Orkis.Sandboxing;
using Orkis.Supervision;

namespace Orkis.Core.Tests;

public sealed class AgentRunnerTests : IDisposable
{
    private readonly FakeChatClient _chatClient = new();
    private readonly FakeTool _tool = new();
    private readonly ScriptedSupervisor _supervisor = new();
    private readonly FakeSupervisorResolver _resolver;
    private readonly InMemoryCheckpointStore _checkpointStore = new();

    public AgentRunnerTests() => _resolver = new FakeSupervisorResolver(_supervisor);

    public void Dispose() => _chatClient.Dispose();

    private AgentRunner CreateRunner() => new(_chatClient, [_tool], _resolver, _checkpointStore, TimeProvider.System);

    private static ChatResponse TextResponse(string text, long inputTokens = 10, long outputTokens = 5) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
        };

    private static ChatResponse ToolCallResponse(string callId, string toolName)
    {
        FunctionCallContent callContent = new(callId, toolName, new Dictionary<string, object?> { ["arg"] = "value" });
        return new(new ChatMessage(ChatRole.Assistant, [callContent]))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
        };
    }

    [Fact]
    public async Task CompletesWhenModelReturnsNoToolCalls()
    {
        _chatClient.Enqueue(TextResponse("all done"));

        var result = await CreateRunner().StartAsync(new AgentRunRequest { Prompt = "hello" });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("all done", result.FinalText);
        Assert.Equal(15, result.Usage.InputTokens + result.Usage.OutputTokens);
        Assert.Empty(_supervisor.Reviewed);
    }

    [Fact]
    public async Task ExecutesApprovedToolCallAndFeedsResultBackToModel()
    {
        _chatClient.Enqueue(ToolCallResponse("call-1", "fake_tool"));
        _chatClient.Enqueue(TextResponse("done"));

        var result = await CreateRunner().StartAsync(new AgentRunRequest { Prompt = "use the tool" });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, _tool.Invocations);
        Assert.Equal(1, result.Usage.ToolCalls);
        Assert.Single(_supervisor.Reviewed);

        var secondRequest = _chatClient.Requests[1];
        var toolMessage = Assert.Single(secondRequest, m => m.Role == ChatRole.Tool);
        var content = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Equal("call-1", content.CallId);
    }

    [Fact]
    public async Task DeniedToolCallIsNotExecutedAndModelSeesTheReason()
    {
        _chatClient.Enqueue(ToolCallResponse("call-1", "fake_tool"));
        _chatClient.Enqueue(TextResponse("understood"));
        _supervisor.Enqueue(SupervisionDecision.Deny("too risky"));

        var result = await CreateRunner().StartAsync(new AgentRunRequest { Prompt = "use the tool" });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(0, _tool.Invocations);
        Assert.Equal(0, result.Usage.ToolCalls);

        var toolMessage = Assert.Single(_chatClient.Requests[1], m => m.Role == ChatRole.Tool);
        var content = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Contains("too risky", content.Result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingDecisionPausesRunAndResumeCompletesIt()
    {
        _chatClient.Enqueue(ToolCallResponse("call-1", "fake_tool"));
        _chatClient.Enqueue(TextResponse("done after approval"));
        _supervisor.Enqueue(SupervisionDecision.Defer());

        var runner = CreateRunner();
        var paused = await runner.StartAsync(new AgentRunRequest { RunId = "run-1", Prompt = "use the tool" });

        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);
        Assert.Equal(0, _tool.Invocations);
        Assert.NotNull(await _checkpointStore.LoadLatestAsync("run-1"));

        // The supervisor's script is now empty, so re-review on resume approves.
        var resumed = await runner.ResumeAsync("run-1");

        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal("done after approval", resumed.FinalText);
        Assert.Equal(1, _tool.Invocations);
        Assert.Equal(2, _supervisor.Reviewed.Count);
    }

    [Fact]
    public async Task ToolCallBudgetStopsRunBeforeExecution()
    {
        _chatClient.Enqueue(ToolCallResponse("call-1", "fake_tool"));

        var result = await CreateRunner()
            .StartAsync(
                new AgentRunRequest
                {
                    Prompt = "use the tool",
                    Budget = new RunBudget { MaxToolCalls = 0 },
                }
            );

        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
        Assert.Equal(0, _tool.Invocations);
    }

    [Fact]
    public async Task TokenBudgetStopsRun()
    {
        _chatClient.Enqueue(ToolCallResponse("call-1", "fake_tool"));

        var result = await CreateRunner()
            .StartAsync(
                new AgentRunRequest
                {
                    Prompt = "use the tool",
                    Budget = new RunBudget { MaxTokens = 5 },
                }
            );

        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
        Assert.Equal(0, _tool.Invocations);
    }

    [Fact]
    public async Task SandboxRequirementRejectsToolThatCannotRunSandboxed()
    {
        _chatClient.Enqueue(ToolCallResponse("call-1", "fake_tool"));
        _chatClient.Enqueue(TextResponse("acknowledged"));
        _supervisor.Enqueue(SupervisionDecision.Approve(SandboxLevel.Strict));

        var result = await CreateRunner().StartAsync(new AgentRunRequest { Prompt = "use the tool" });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(0, _tool.Invocations);

        var toolMessage = Assert.Single(_chatClient.Requests[1], m => m.Role == ChatRole.Tool);
        var content = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Contains("sandbox", content.Result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupervisorKeyFlowsToResolverAndSurvivesResume()
    {
        _chatClient.Enqueue(ToolCallResponse("call-1", "fake_tool"));
        _chatClient.Enqueue(TextResponse("done"));
        _supervisor.Enqueue(SupervisionDecision.Defer());

        var runner = CreateRunner();
        var request = new AgentRunRequest
        {
            RunId = "run-1",
            Prompt = "use the tool",
            SupervisorKey = "strict",
        };

        var paused = await runner.StartAsync(request);
        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);

        var resumed = await runner.ResumeAsync("run-1");
        Assert.Equal(RunStatus.Completed, resumed.Status);

        // Both segments — including the one rehydrated from the checkpoint — used the run's key.
        Assert.Equal(["strict", "strict"], _resolver.RequestedKeys);
    }

    [Fact]
    public async Task UnknownToolProducesErrorResultWithoutSupervision()
    {
        _chatClient.Enqueue(ToolCallResponse("call-1", "no_such_tool"));
        _chatClient.Enqueue(TextResponse("acknowledged"));

        var result = await CreateRunner().StartAsync(new AgentRunRequest { Prompt = "use a missing tool" });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Empty(_supervisor.Reviewed);

        var toolMessage = Assert.Single(_chatClient.Requests[1], m => m.Role == ChatRole.Tool);
        var content = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Contains("Unknown tool", content.Result?.ToString(), StringComparison.Ordinal);
    }
}
