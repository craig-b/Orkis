using Microsoft.Extensions.AI;

namespace Orkis.Host;

/// <summary>
/// A scripted stand-in for a real model so the demo runs without an API key:
/// checks the time, discovers the dice tool via the catalogue and rolls, runs a
/// sandboxed shell command that writes a file into the persistent workspace,
/// promotes that file to the artifact store, then summarizes.
/// </summary>
internal sealed class OfflineChatClient : IChatClient
{
    private readonly string _artifactName = $"greeting-{Guid.CreateVersion7().ToString("n")[..8]}.txt";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        // Position in the script comes from the transcript, not instance state, so a
        // resumed run continues where it paused instead of replaying from the start —
        // the same behavior a real model gets from seeing the conversation history.
        var turn = messages.Count(static m => m.Role == ChatRole.Assistant) + 1;
        FunctionCallContent timeCall = new("offline-1", "get_utc_now", new Dictionary<string, object?>());
        FunctionCallContent searchCall = new(
            "offline-2",
            "search_tools",
            new Dictionary<string, object?> { ["query"] = "dice" }
        );
        FunctionCallContent diceCall = new(
            "offline-3",
            "roll_dice",
            new Dictionary<string, object?> { ["count"] = 3 }
        );
        FunctionCallContent shellCall = new(
            "offline-4",
            "run_shell_command",
            new Dictionary<string, object?>
            {
                ["command"] = "echo Hello from the Orkis sandbox > greeting.txt; cat greeting.txt; pwd",
            }
        );
        FunctionCallContent promoteCall = new(
            "offline-5",
            "promote_artifact",
            new Dictionary<string, object?> { ["path"] = "greeting.txt", ["name"] = _artifactName }
        );

        var response = turn switch
        {
            1 => Scripted("I'll check the current time first.", timeCall),
            2 => Scripted("I need a dice tool; let me search the catalogue.", searchCall),
            3 => Scripted("Found it — rolling three dice.", diceCall),
            4 => Scripted("Now I'll run the requested command in the sandbox.", shellCall),
            5 => Scripted("I'll promote the greeting file to the artifact store.", promoteCall),
            _ => Scripted(
                "Done: I checked the time, rolled the dice, ran the command, and promoted the output "
                    + "as an artifact. (offline scripted response)"
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
