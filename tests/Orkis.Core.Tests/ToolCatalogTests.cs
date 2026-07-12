using Microsoft.Extensions.AI;
using Orkis.Agents;
using Orkis.Core.Tools;
using Orkis.Runs;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Core.Tests;

public sealed class InMemoryToolCatalogTests
{
    private static InMemoryToolCatalog CreateCatalog() =>
        new([new FakeTool("roll_dice"), new FakeTool("draw_card"), new FakeTool("shuffle_deck")]);

    [Fact]
    public async Task SearchMatchesNameTermsCaseInsensitively()
    {
        var matches = await CreateCatalog().SearchAsync("DICE");

        Assert.Equal("roll_dice", Assert.Single(matches).Name);
    }

    [Fact]
    public async Task SearchRanksMoreMatchedTermsFirstAndHonorsLimit()
    {
        var catalog = CreateCatalog();

        var matches = await catalog.SearchAsync("draw card deck", limit: 2);

        Assert.Equal(2, matches.Count);
        Assert.Equal("draw_card", matches[0].Name);
    }

    [Fact]
    public async Task SearchWithNoMatchesReturnsEmpty()
    {
        Assert.Empty(await CreateCatalog().SearchAsync("teleport"));
    }

    [Fact]
    public async Task ResolveReturnsToolOrNull()
    {
        var catalog = CreateCatalog();

        Assert.NotNull(await catalog.ResolveAsync("roll_dice"));
        Assert.Null(await catalog.ResolveAsync("missing"));
    }
}

public sealed class ToolScopingTests : IDisposable
{
    private readonly FakeChatClient _chatClient = new();
    private readonly ScriptedSupervisor _supervisor = new();
    private readonly InMemoryCheckpointStore _checkpointStore = new();

    public void Dispose() => _chatClient.Dispose();

    private AgentRunner CreateRunner(IEnumerable<ITool> coreTools) =>
        new(
            _chatClient,
            coreTools,
            new FakeSupervisorResolver(_supervisor),
            _checkpointStore,
            TimeProvider.System
        );

    private static List<string> DeclaredToolNames(ChatOptions? options) =>
        options?.Tools?.Select(tool => tool.Name).ToList() ?? [];

    private static string ToolResultText(ChatMessage message) =>
        Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents)).Result?.ToString() ?? "";

    [Fact]
    public async Task ToolNamesRestrictsTheDeclaredCoreTools()
    {
        _chatClient.Enqueue(TestResponses.Text("done"));
        var runner = CreateRunner([new FakeTool("alpha"), new FakeTool("beta")]);

        await runner.StartAsync(new AgentRunRequest { Prompt = "go", ToolNames = ["beta"] });

        Assert.Equal(["beta"], DeclaredToolNames(Assert.Single(_chatClient.RequestOptions)));
    }

    [Fact]
    public async Task UnknownToolNamesFailFast()
    {
        var runner = CreateRunner([new FakeTool("alpha")]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.StartAsync(new AgentRunRequest { Prompt = "go", ToolNames = ["alpha", "nope"] })
        );
    }

    [Fact]
    public async Task RestrictedToolIsUnknownWhenCalled()
    {
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "alpha"));
        _chatClient.Enqueue(TestResponses.Text("done"));
        var hidden = new FakeTool("alpha");
        var runner = CreateRunner([hidden, new FakeTool("beta")]);

        var result = await runner.StartAsync(new AgentRunRequest { Prompt = "go", ToolNames = ["beta"] });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(0, hidden.Invocations);
        var toolMessage = _chatClient.Requests[1].Single(m => m.Role == ChatRole.Tool);
        Assert.Contains("Unknown tool 'alpha'", ToolResultText(toolMessage), StringComparison.Ordinal);
    }
}

public sealed class ProgressiveDisclosureTests : IDisposable
{
    private readonly FakeChatClient _chatClient = new();
    private readonly ScriptedSupervisor _supervisor = new();
    private readonly InMemoryCheckpointStore _checkpointStore = new();
    private readonly FakeTool _coreTool = new("core_tool");
    private readonly FakeTool _catalogTool = new("roll_dice");

    public void Dispose() => _chatClient.Dispose();

    private AgentRunner CreateRunner(IToolCatalog catalog) =>
        new(
            _chatClient,
            [_coreTool],
            new FakeSupervisorResolver(_supervisor),
            _checkpointStore,
            TimeProvider.System,
            costCalculator: null,
            toolCatalog: catalog
        );

    private static ChatResponse SearchCall(string query = "dice")
    {
        FunctionCallContent call = new(
            "search-1",
            "search_tools",
            new Dictionary<string, object?> { ["query"] = query }
        );
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
        };
    }

    private static List<string> DeclaredToolNames(ChatOptions? options) =>
        options?.Tools?.Select(tool => tool.Name).ToList() ?? [];

    private static string ToolResultText(ChatMessage message) =>
        Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents)).Result?.ToString() ?? "";

    [Fact]
    public async Task SearchToolsIsDeclaredAndCatalogToolsAreNot()
    {
        _chatClient.Enqueue(TestResponses.Text("done"));
        var runner = CreateRunner(new InMemoryToolCatalog([_catalogTool]));

        await runner.StartAsync(new AgentRunRequest { Prompt = "go" });

        Assert.Equal(["core_tool", "search_tools"], DeclaredToolNames(_chatClient.RequestOptions[0]));
    }

    [Fact]
    public async Task SearchActivatesMatchesForSubsequentCallsAndExecution()
    {
        _chatClient.Enqueue(SearchCall());
        _chatClient.Enqueue(TestResponses.ToolCall("call-2", "roll_dice"));
        _chatClient.Enqueue(TestResponses.Text("rolled"));
        var runner = CreateRunner(new InMemoryToolCatalog([_catalogTool]));

        var result = await runner.StartAsync(new AgentRunRequest { Prompt = "roll dice" });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, _catalogTool.Invocations);

        // First call: only core + search declared. After activation: dice appears.
        Assert.Equal(["core_tool", "search_tools"], DeclaredToolNames(_chatClient.RequestOptions[0]));
        Assert.Equal(["core_tool", "roll_dice", "search_tools"], DeclaredToolNames(_chatClient.RequestOptions[1]));

        var searchResult = _chatClient.Requests[1].Single(m => m.Role == ChatRole.Tool);
        Assert.Contains("roll_dice", ToolResultText(searchResult), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchWithNoMatchesTellsTheModel()
    {
        _chatClient.Enqueue(SearchCall("teleport"));
        _chatClient.Enqueue(TestResponses.Text("done"));
        var runner = CreateRunner(new InMemoryToolCatalog([_catalogTool]));

        await runner.StartAsync(new AgentRunRequest { Prompt = "go" });

        var searchResult = _chatClient.Requests[1].Single(m => m.Role == ChatRole.Tool);
        Assert.Contains("No tools matched", ToolResultText(searchResult), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchGoesThroughSupervision()
    {
        _supervisor.Enqueue(SupervisionDecision.Deny("no browsing the catalogue"));
        _chatClient.Enqueue(SearchCall());
        _chatClient.Enqueue(TestResponses.Text("done"));
        var runner = CreateRunner(new InMemoryToolCatalog([_catalogTool]));

        await runner.StartAsync(new AgentRunRequest { Prompt = "go" });

        var proposed = Assert.Single(_supervisor.Reviewed);
        Assert.Equal("search_tools", proposed.Tool.Name);
        var searchResult = _chatClient.Requests[1].Single(m => m.Role == ChatRole.Tool);
        Assert.Contains("denied", ToolResultText(searchResult), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["core_tool", "search_tools"], DeclaredToolNames(_chatClient.RequestOptions[1]));
    }

    [Fact]
    public async Task ActivatedToolsSurviveResumeThroughACheckpoint()
    {
        // Segment one: search activates the dice tool, then the run pauses on it.
        _chatClient.Enqueue(SearchCall());
        _chatClient.Enqueue(TestResponses.ToolCall("call-2", "roll_dice"));
        _supervisor.Enqueue(SupervisionDecision.Approve()); // the search
        _supervisor.Enqueue(SupervisionDecision.Defer()); // the dice call pauses the run

        var catalog = new InMemoryToolCatalog([_catalogTool]);
        var paused = await CreateRunner(catalog).StartAsync(new AgentRunRequest { RunId = "run-1", Prompt = "go" });
        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);

        // Fresh runner over the same checkpoint store: activation must be restored.
        _chatClient.Enqueue(TestResponses.Text("rolled"));
        var resumed = await CreateRunner(catalog).ResumeAsync("run-1");

        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal(1, _catalogTool.Invocations);
        Assert.Contains("roll_dice", DeclaredToolNames(_chatClient.RequestOptions[^1]));
    }

    [Fact]
    public async Task VanishedCatalogToolIsDroppedAndCallingItErrs()
    {
        _chatClient.Enqueue(SearchCall());
        _chatClient.Enqueue(TestResponses.ToolCall("call-2", "roll_dice"));
        _supervisor.Enqueue(SupervisionDecision.Approve());
        _supervisor.Enqueue(SupervisionDecision.Defer());

        var paused = await CreateRunner(new InMemoryToolCatalog([_catalogTool]))
            .StartAsync(new AgentRunRequest { RunId = "run-1", Prompt = "go" });
        Assert.Equal(RunStatus.AwaitingSupervision, paused.Status);

        // The catalogue no longer carries the tool when the run resumes.
        _chatClient.Enqueue(TestResponses.Text("done"));
        var resumed = await CreateRunner(new InMemoryToolCatalog([])).ResumeAsync("run-1");

        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal(0, _catalogTool.Invocations);
        var toolMessage = _chatClient.Requests[^1].Last(m => m.Role == ChatRole.Tool);
        Assert.Contains("Unknown tool 'roll_dice'", ToolResultText(toolMessage), StringComparison.Ordinal);
        Assert.DoesNotContain("roll_dice", DeclaredToolNames(_chatClient.RequestOptions[^1]));
    }
}
