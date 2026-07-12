using Microsoft.Extensions.AI;
using Orkis.Agents;
using Orkis.Clients;
using Orkis.Runs;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Core.Tests;

public sealed class ModelSelectionTests
{
    private sealed class FakeChatClientResolver(Dictionary<string, IChatClient> clients) : IChatClientResolver
    {
        public IChatClient Resolve(string modelKey) =>
            clients.TryGetValue(modelKey, out var client)
                ? client
                : throw new InvalidOperationException($"No chat client is registered under model key '{modelKey}'.");
    }

    private static AgentRunner Runner(
        IChatClient defaultClient,
        IChatClientResolver? resolver,
        ISupervisor supervisor,
        ICheckpointStore? checkpoints = null,
        ITool[]? tools = null
    ) =>
        new(
            defaultClient,
            tools ?? [],
            new FakeSupervisorResolver(supervisor),
            checkpoints ?? new InMemoryCheckpointStore(),
            TimeProvider.System,
            chatClientResolver: resolver
        );

    [Fact]
    public async Task ModelKeyRoutesTheRunToTheKeyedClient()
    {
        using var defaultClient = new FakeChatClient();
        using var altClient = new FakeChatClient();
        altClient.Enqueue(TestResponses.Text("from alt"));
        var runner = Runner(
            defaultClient,
            new FakeChatClientResolver(new() { ["alt"] = altClient }),
            new AutoApproveSupervisor()
        );

        var result = await runner.StartAsync(new AgentRunRequest { Prompt = "p", ModelKey = "alt" });

        Assert.Equal("from alt", result.FinalText);
        Assert.Empty(defaultClient.Requests);
        Assert.Single(altClient.Requests);
    }

    [Fact]
    public async Task NoModelKeyUsesTheDefaultClient()
    {
        using var defaultClient = new FakeChatClient();
        defaultClient.Enqueue(TestResponses.Text("from default"));
        var runner = Runner(defaultClient, resolver: null, new AutoApproveSupervisor());

        var result = await runner.StartAsync(new AgentRunRequest { Prompt = "p" });

        Assert.Equal("from default", result.FinalText);
    }

    [Fact]
    public async Task UnknownModelKeyFailsFast()
    {
        using var defaultClient = new FakeChatClient();
        var runner = Runner(defaultClient, new FakeChatClientResolver([]), new AutoApproveSupervisor());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.StartAsync(new AgentRunRequest { Prompt = "p", ModelKey = "missing" })
        );
        Assert.Empty(defaultClient.Requests);
    }

    [Fact]
    public async Task ModelKeyWithoutARegistryFailsFast()
    {
        using var defaultClient = new FakeChatClient();
        var runner = Runner(defaultClient, resolver: null, new AutoApproveSupervisor());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.StartAsync(new AgentRunRequest { Prompt = "p", ModelKey = "alt" })
        );
        Assert.Contains("AddOrkisChatClient", ex.Message);
    }

    [Fact]
    public async Task ResumedRunReconnectsToItsModelKey()
    {
        using var defaultClient = new FakeChatClient();
        using var altClient = new FakeChatClient();
        altClient.Enqueue(TestResponses.ToolCall("call-1", "fake_tool"));
        altClient.Enqueue(TestResponses.Text("done after approval"));

        var inbox = new InMemoryApprovalInbox();
        var supervisor = new QueueSupervisor(inbox, TimeProvider.System);
        var checkpoints = new InMemoryCheckpointStore();
        var resolver = new FakeChatClientResolver(new() { ["alt"] = altClient });
        var tool = new FakeTool(risk: ToolRisk.Destructive);

        var paused = await Runner(defaultClient, resolver, supervisor, checkpoints, [tool])
            .StartAsync(
                new AgentRunRequest
                {
                    RunId = "run-1",
                    Prompt = "use the tool",
                    ModelKey = "alt",
                }
            );
        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);

        await inbox.DecideAsync("run-1", "call-1", SupervisionDecision.Approve());

        // A fresh runner (fresh process, same stores): the checkpointed key routes
        // the resumed segment back to the keyed client.
        var resumed = await Runner(defaultClient, resolver, supervisor, checkpoints, [tool]).ResumeAsync("run-1");

        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal("done after approval", resumed.FinalText);
        Assert.Empty(defaultClient.Requests);
    }
}
