using Microsoft.Extensions.AI;

namespace Orkis.Host;

/// <summary>
/// A scripted stand-in for a real model so the demo runs without an API key:
/// checks the time, runs a sandboxed shell command that writes a file into the
/// persistent workspace, promotes that file to the artifact store, then summarizes.
/// </summary>
internal sealed class OfflineChatClient : IChatClient
{
    private readonly string _artifactName = $"greeting-{Guid.CreateVersion7().ToString("n")[..8]}.txt";
    private int _turn;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        _turn++;
        FunctionCallContent timeCall = new("offline-1", "get_utc_now", new Dictionary<string, object?>());
        FunctionCallContent shellCall = new(
            "offline-2",
            "run_shell_command",
            new Dictionary<string, object?>
            {
                ["command"] = "echo Hello from the Orkis sandbox > greeting.txt; cat greeting.txt; pwd",
            }
        );
        FunctionCallContent promoteCall = new(
            "offline-3",
            "promote_artifact",
            new Dictionary<string, object?> { ["path"] = "greeting.txt", ["name"] = _artifactName }
        );

        var response = _turn switch
        {
            1 => Scripted("I'll check the current time first.", timeCall),
            2 => Scripted("Now I'll run the requested command in the sandbox.", shellCall),
            3 => Scripted("I'll promote the greeting file to the artifact store.", promoteCall),
            _ => Scripted(
                "Done: I checked the time, ran the command, and promoted the output as an artifact. "
                    + "(offline scripted response)"
            ),
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
