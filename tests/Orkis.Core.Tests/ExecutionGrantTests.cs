using Orkis.Agents;
using Orkis.Runs;
using Orkis.Sandboxing;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Core.Tests;

public sealed class ExecutionGrantTests : IDisposable
{
    private readonly FakeChatClient _chatClient = new();
    private readonly ScriptedSupervisor _supervisor = new();
    private readonly InMemoryCheckpointStore _checkpointStore = new();

    public void Dispose() => _chatClient.Dispose();

    private AgentRunner CreateRunner(ITool tool) =>
        new(_chatClient, [tool], new FakeSupervisorResolver(_supervisor), _checkpointStore, TimeProvider.System);

    [Fact]
    public async Task SandboxAndNetworkGrantsReachTheTool()
    {
        var tool = new FakeSandboxedTool();
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "sandboxed_tool"));
        _chatClient.Enqueue(TestResponses.Text("done"));
        _supervisor.Enqueue(SupervisionDecision.Approve(SandboxLevel.Strict, NetworkMode.RestrictedEgress));

        var result = await CreateRunner(tool).StartAsync(new AgentRunRequest { Prompt = "go" });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.NotNull(tool.LastGrant);
        Assert.Equal(SandboxLevel.Strict, tool.LastGrant.MinimumSandboxLevel);
        Assert.Equal(NetworkMode.RestrictedEgress, tool.LastGrant.Network);
    }

    [Fact]
    public async Task NetworkOnlyGrantFlowsWithoutASandboxLevel()
    {
        var tool = new FakeSandboxedTool();
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "sandboxed_tool"));
        _chatClient.Enqueue(TestResponses.Text("done"));
        _supervisor.Enqueue(SupervisionDecision.Approve(grantedNetwork: NetworkMode.RestrictedEgress));

        await CreateRunner(tool).StartAsync(new AgentRunRequest { Prompt = "go" });

        Assert.NotNull(tool.LastGrant);
        Assert.Null(tool.LastGrant.MinimumSandboxLevel);
        Assert.Equal(NetworkMode.RestrictedEgress, tool.LastGrant.Network);
    }

    [Fact]
    public async Task PlainApprovalCarriesNoGrant()
    {
        var tool = new FakeSandboxedTool();
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "sandboxed_tool"));
        _chatClient.Enqueue(TestResponses.Text("done"));
        _supervisor.Enqueue(SupervisionDecision.Approve());

        await CreateRunner(tool).StartAsync(new AgentRunRequest { Prompt = "go" });

        Assert.Equal(1, tool.Invocations);
        Assert.Null(tool.LastGrant);
    }

    [Fact]
    public async Task NetworkGrantToAnUnsandboxableToolIsRejected()
    {
        var tool = new FakeTool("plain_tool", ToolRisk.Destructive);
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "plain_tool"));
        _chatClient.Enqueue(TestResponses.Text("done"));
        _supervisor.Enqueue(SupervisionDecision.Approve(grantedNetwork: NetworkMode.RestrictedEgress));

        var result = await CreateRunner(tool).StartAsync(new AgentRunRequest { Prompt = "go" });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(0, tool.Invocations);
    }
}
