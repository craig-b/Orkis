using System.Text.Json;
using Microsoft.Extensions.AI;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Core.Tests;

/// <summary>Builders for scripted chat responses.</summary>
internal static class TestResponses
{
    public static ChatResponse Text(string text, long inputTokens = 10, long outputTokens = 5) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
        };

    public static ChatResponse ToolCall(string callId, string toolName)
    {
        FunctionCallContent callContent = new(callId, toolName, new Dictionary<string, object?> { ["arg"] = "value" });
        return new(new ChatMessage(ChatRole.Assistant, [callContent]))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
        };
    }
}

/// <summary>An <see cref="IChatClient"/> that replays scripted responses in order.</summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new();

    public List<List<ChatMessage>> Requests { get; } = [];

    /// <summary>The options each request carried, so tests can assert declared tools.</summary>
    public List<ChatOptions?> RequestOptions { get; } = [];

    public void Enqueue(ChatResponse response) => _responses.Enqueue(response);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        Requests.Add([.. messages]);
        RequestOptions.Add(options);
        return Task.FromResult(_responses.Dequeue());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>A tool that records invocations and returns a fixed result.</summary>
internal sealed class FakeTool(string name = "fake_tool", ToolRisk risk = ToolRisk.ReadOnly) : ITool
{
    public int Invocations { get; private set; }

    public ToolCall? LastCall { get; private set; }

    public ToolDescriptor Descriptor { get; } =
        new()
        {
            Name = name,
            Description = "A test tool.",
            ParametersSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
            Risk = risk,
        };

    public Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        Invocations++;
        LastCall = toolCall;
        return Task.FromResult(new ToolResult { ToolCallId = toolCall.Id, Content = $"{Descriptor.Name}-output" });
    }
}

/// <summary>A sandbox-capable tool that records the execution grant it received.</summary>
internal sealed class FakeSandboxedTool(string name = "sandboxed_tool") : ISandboxedTool
{
    public int Invocations { get; private set; }

    public Orkis.Sandboxing.ExecutionGrant? LastGrant { get; private set; }

    public ToolDescriptor Descriptor { get; } =
        new()
        {
            Name = name,
            Description = "A sandbox-capable test tool.",
            ParametersSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
            Risk = ToolRisk.Destructive,
        };

    public Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        Invocations++;
        LastGrant = null;
        return Task.FromResult(new ToolResult { ToolCallId = toolCall.Id, Content = "ran ungated" });
    }

    public Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        Orkis.Sandboxing.ExecutionGrant grant,
        CancellationToken cancellationToken = default
    )
    {
        Invocations++;
        LastGrant = grant;
        return Task.FromResult(new ToolResult { ToolCallId = toolCall.Id, Content = "ran granted" });
    }
}

/// <summary>Collects run events in order.</summary>
internal sealed class FakeRunEventSink : Orkis.Runs.IRunEventSink
{
    public List<Orkis.Runs.RunEvent> Events { get; } = [];

    public ValueTask WriteAsync(Orkis.Runs.RunEvent runEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(runEvent);
        return ValueTask.CompletedTask;
    }
}

/// <summary>An in-memory <see cref="Orkis.Memory.IMemoryStore"/> that records writes and scripts search results.</summary>
internal sealed class FakeMemoryStore : Orkis.Memory.IMemoryStore
{
    public List<Orkis.Memory.MemoryEntry> Written { get; } = [];

    public List<(string Query, string Scope)> Searches { get; } = [];

    public List<Scored<Orkis.Memory.MemoryEntry>> SearchResults { get; } = [];

    public Task WriteAsync(Orkis.Memory.MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        Written.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Scored<Orkis.Memory.MemoryEntry>>> SearchAsync(
        string query,
        string scope = Orkis.Memory.MemoryScopes.Global,
        int topK = 8,
        CancellationToken cancellationToken = default
    )
    {
        Searches.Add((query, scope));
        return Task.FromResult<IReadOnlyList<Scored<Orkis.Memory.MemoryEntry>>>([.. SearchResults.Take(topK)]);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A cost calculator that charges a fixed amount per model call.</summary>
internal sealed class FixedCostCalculator(decimal perCall) : Orkis.Runs.ICostCalculator
{
    public decimal Calculate(Orkis.Runs.TokenUsage usage) => perCall;
}

/// <summary>A resolver that returns one fixed supervisor and records the keys requested.</summary>
internal sealed class FakeSupervisorResolver(ISupervisor supervisor) : ISupervisorResolver
{
    public List<string> RequestedKeys { get; } = [];

    public ISupervisor Resolve(string key)
    {
        RequestedKeys.Add(key);
        return supervisor;
    }
}

/// <summary>A supervisor that replays scripted decisions, approving once the script runs out.</summary>
internal sealed class ScriptedSupervisor : ISupervisor
{
    private readonly Queue<SupervisionDecision> _decisions = new();

    public List<ProposedAction> Reviewed { get; } = [];

    public void Enqueue(SupervisionDecision decision) => _decisions.Enqueue(decision);

    public Task<SupervisionDecision> ReviewAsync(ProposedAction action, CancellationToken cancellationToken = default)
    {
        Reviewed.Add(action);
        return Task.FromResult(_decisions.Count > 0 ? _decisions.Dequeue() : SupervisionDecision.Approve());
    }
}
