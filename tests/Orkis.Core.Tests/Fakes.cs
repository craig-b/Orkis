using System.Text.Json;
using Microsoft.Extensions.AI;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Core.Tests;

/// <summary>An <see cref="IChatClient"/> that replays scripted responses in order.</summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new();

    public List<List<ChatMessage>> Requests { get; } = [];

    public void Enqueue(ChatResponse response) => _responses.Enqueue(response);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        Requests.Add([.. messages]);
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
