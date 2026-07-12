using System.Text.Json;
using Microsoft.Extensions.AI;
using Orkis.Agents;
using Orkis.Memory;
using Orkis.Runs;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Core.Tests;

public sealed class MemoryToolTests
{
    private readonly FakeMemoryStore _store = new();

    private static ToolCall Call(string toolName, string argumentsJson) =>
        new()
        {
            Id = "call-1",
            ToolName = toolName,
            Arguments = JsonSerializer.Deserialize<JsonElement>(argumentsJson),
        };

    private static MemoryEntry Entry(string id, string text, string scope = MemoryScopes.Global) =>
        new()
        {
            Id = id,
            Text = text,
            Scope = scope,
            CreatedAt = new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.Zero),
        };

    [Fact]
    public async Task SaveDefaultsToTheRunScope()
    {
        var tool = new SaveMemoryTool(_store, scope: "cron-report");

        var result = await tool.InvokeAsync(Call("save_memory", """{"text":"The report needs UTC dates."}"""));

        Assert.False(result.IsError);
        var written = Assert.Single(_store.Written);
        Assert.Equal("cron-report", written.Scope);
        Assert.Equal("The report needs UTC dates.", written.Text);
        Assert.Contains("cron-report", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveHonorsAnExplicitScopeArgument()
    {
        var tool = new SaveMemoryTool(_store, scope: "cron-report");

        await tool.InvokeAsync(Call("save_memory", """{"text":"Everyone should know this.","scope":"global"}"""));

        Assert.Equal(MemoryScopes.Global, Assert.Single(_store.Written).Scope);
    }

    [Fact]
    public async Task SaveIsMutatingAndSearchIsReadOnly()
    {
        Assert.Equal(ToolRisk.Mutating, new SaveMemoryTool(_store).Descriptor.Risk);
        Assert.Equal(ToolRisk.ReadOnly, new SearchMemoriesTool(_store).Descriptor.Risk);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SearchUsesTheRunScopeAndRendersProvenance()
    {
        _store.SearchResults.Add(new Scored<MemoryEntry>(Entry("m1", "User prefers tabs.", "cron-report"), 0.9));
        var tool = new SearchMemoriesTool(_store, scope: "cron-report");

        var result = await tool.InvokeAsync(Call("search_memories", """{"query":"preferences"}"""));

        Assert.Equal(("preferences", "cron-report"), Assert.Single(_store.Searches));
        Assert.Contains("[m1]", result.Content, StringComparison.Ordinal);
        Assert.Contains("cron-report", result.Content, StringComparison.Ordinal);
        Assert.Contains("2026-07-12", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchWithNoResultsSaysSo()
    {
        var tool = new SearchMemoriesTool(_store);

        var result = await tool.InvokeAsync(Call("search_memories", """{"query":"anything"}"""));

        Assert.Contains("No relevant memories", result.Content, StringComparison.Ordinal);
    }
}

public sealed class MemoryInjectionTests : IDisposable
{
    private readonly FakeChatClient _chatClient = new();
    private readonly ScriptedSupervisor _supervisor = new();
    private readonly InMemoryCheckpointStore _checkpointStore = new();
    private readonly FakeMemoryStore _memoryStore = new();

    public void Dispose() => _chatClient.Dispose();

    private AgentRunner CreateRunner(IMemoryStore? memoryStore) =>
        new(
            _chatClient,
            [new FakeTool()],
            new FakeSupervisorResolver(_supervisor),
            _checkpointStore,
            TimeProvider.System,
            costCalculator: null,
            toolCatalog: null,
            memoryStore: memoryStore
        );

    private static MemoryEntry Entry(string id, string text) =>
        new()
        {
            Id = id,
            Text = text,
            CreatedAt = new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.Zero),
        };

    [Fact]
    public async Task RelevantMemoriesInjectIntoTheSystemPromptWithFraming()
    {
        _memoryStore.SearchResults.Add(new Scored<MemoryEntry>(Entry("m1", "User prefers tabs."), 0.9));
        _chatClient.Enqueue(TestResponses.Text("done"));

        await CreateRunner(_memoryStore)
            .StartAsync(
                new AgentRunRequest
                {
                    Prompt = "fix the formatting",
                    SystemPrompt = "You are the agent.",
                    MemoryScope = "project-x",
                }
            );

        Assert.Equal(("fix the formatting", "project-x"), Assert.Single(_memoryStore.Searches));
        var system = Assert.Single(_chatClient.Requests)[0];
        Assert.Equal(ChatRole.System, system.Role);
        Assert.StartsWith("You are the agent.", system.Text, StringComparison.Ordinal);
        Assert.Contains("Recalled memories", system.Text, StringComparison.Ordinal);
        Assert.Contains("unverified", system.Text, StringComparison.Ordinal);
        Assert.Contains("User prefers tabs.", system.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoMemoriesMeansNoInjection()
    {
        _chatClient.Enqueue(TestResponses.Text("done"));

        await CreateRunner(_memoryStore)
            .StartAsync(new AgentRunRequest { Prompt = "go", SystemPrompt = "You are the agent." });

        var system = Assert.Single(_chatClient.Requests)[0];
        Assert.Equal("You are the agent.", system.Text);
    }

    [Fact]
    public async Task NoStoreMeansNoSearchAndNoInjection()
    {
        _chatClient.Enqueue(TestResponses.Text("done"));

        await CreateRunner(memoryStore: null).StartAsync(new AgentRunRequest { Prompt = "go" });

        Assert.Empty(_memoryStore.Searches);
        Assert.Equal(ChatRole.User, Assert.Single(_chatClient.Requests)[0].Role);
    }

    [Fact]
    public async Task ResumeDoesNotReInjectMemories()
    {
        _memoryStore.SearchResults.Add(new Scored<MemoryEntry>(Entry("m1", "User prefers tabs."), 0.9));
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "fake_tool"));
        _supervisor.Enqueue(SupervisionDecision.Defer());

        var runner = CreateRunner(_memoryStore);
        var paused = await runner.StartAsync(new AgentRunRequest { RunId = "run-1", Prompt = "go" });
        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);

        _chatClient.Enqueue(TestResponses.Text("done"));
        await runner.ResumeAsync("run-1");

        // One search at start, none on resume; the recalled block appears exactly once.
        Assert.Single(_memoryStore.Searches);
        var lastRequest = _chatClient.Requests[^1];
        var systemText = lastRequest.First(m => m.Role == ChatRole.System).Text;
        Assert.Equal(1, CountOccurrences(systemText, "Recalled memories"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
