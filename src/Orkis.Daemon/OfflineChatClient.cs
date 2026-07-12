using Microsoft.Extensions.AI;

namespace Orkis.Daemon;

/// <summary>
/// A scripted stand-in for a real model so the daemon runs without an API key: one
/// sandboxed shell command, then a summary. Under the daemon's default queue
/// supervision the shell call parks in the approval inbox, which makes the offline
/// script a complete exercise of the wire protocol: start, pause, approve, resume.
/// </summary>
internal sealed class OfflineChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        // Position in the script comes from the transcript, not instance state, so a
        // resumed run continues where it paused instead of replaying from the start.
        var turn = messages.Count(static m => m.Role == ChatRole.Assistant) + 1;
        FunctionCallContent shellCall = new(
            "offline-1",
            "run_shell_command",
            new Dictionary<string, object?> { ["command"] = "echo Hello from the Orkis daemon; pwd" }
        );

        var response = turn switch
        {
            1 => Scripted("I'll run the greeting command in the sandbox.", shellCall),
            _ => Scripted("Done: I ran the greeting command in the sandbox. (offline scripted response)"),
        };
        return Task.FromResult(response);
    }

    private static ChatResponse Scripted(string text, AIContent? extra = null)
    {
        var contents = new List<AIContent> { new TextContent(text) };
        if (extra is not null)
        {
            contents.Add(extra);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = "offline-script",
            Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 20 },
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
