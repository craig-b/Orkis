using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Orkis.Sandboxing;

namespace Orkis.Supervision;

/// <summary>
/// An LLM approval policy: renders each proposed action (tool, declared risk,
/// arguments) into a review prompt and maps the model's verdict to approve —
/// optionally with a required sandbox level — deny with a reason, or escalate to an
/// inner supervisor. Anything unexpected (malformed output, unknown verdicts) also
/// escalates: when the reviewer is unintelligible, a human decides. Compose behind a
/// <see cref="ThresholdSupervisor"/> so read-only tools skip review, and escalate
/// into a <see cref="QueueSupervisor"/> so hard calls park in the approval inbox.
/// </summary>
public sealed class ChatClientSupervisor : ISupervisor
{
    private const string Instructions =
        "You are the security supervisor for an autonomous agent. Decide whether the "
        + "proposed tool call may execute. Respond with only a JSON object: "
        + """{"verdict": "approve" | "deny" | "escalate", "reason": "<short>", "sandbox": "none" | "standard" | "strict"}"""
        + " — approve when the action is safe for its stated purpose; prefer approving "
        + "with a stronger \"sandbox\" over denying when isolation addresses the risk; "
        + "deny only what is clearly harmful or against policy, with a reason the agent "
        + "can act on; escalate when a human should decide.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IChatClient _chatClient;
    private readonly ISupervisor _escalation;
    private readonly ChatClientSupervisorOptions _options;

    public ChatClientSupervisor(
        IChatClient chatClient,
        ISupervisor escalation,
        IOptions<ChatClientSupervisorOptions>? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(escalation);

        _chatClient = chatClient;
        _escalation = escalation;
        _options = options?.Value ?? new ChatClientSupervisorOptions();
    }

    /// <inheritdoc />
    public async Task<SupervisionDecision> ReviewAsync(
        ProposedAction action,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(action);

        var system = _options.Policy is { Length: > 0 } policy
            ? Instructions + "\n\nHost policy:\n" + policy
            : Instructions;

        var response = await _chatClient
            .GetResponseAsync(
                [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, BuildPrompt(action))],
                new ChatOptions { Temperature = 0 },
                cancellationToken
            )
            .ConfigureAwait(false);

        if (ParseVerdict(response.Text) is { } decision)
        {
            return decision;
        }

        return await _escalation.ReviewAsync(action, cancellationToken).ConfigureAwait(false);
    }

    private string BuildPrompt(ProposedAction action)
    {
        var arguments = action.Call.Arguments.GetRawText();
        if (arguments.Length > _options.MaxArgumentCharacters)
        {
            arguments = arguments[.._options.MaxArgumentCharacters] + "… [truncated]";
        }

        var builder = new StringBuilder();
        builder
            .Append("Tool: ")
            .Append(action.Tool.Name)
            .Append(" (declared risk: ")
            .Append(action.Tool.Risk)
            .AppendLine(")");
        builder.Append("Purpose: ").AppendLine(action.Tool.Description);
        builder.AppendLine("Arguments:");
        builder.AppendLine(arguments);
        return builder.ToString();
    }

    /// <summary>
    /// Maps the reviewer's JSON to a decision, or <see langword="null"/> when the
    /// output is unusable in any way — the caller escalates rather than guessing.
    /// </summary>
    private static SupervisionDecision? ParseVerdict(string responseText)
    {
        var start = responseText.IndexOf('{', StringComparison.Ordinal);
        var end = responseText.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        Verdict? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Verdict>(responseText[start..(end + 1)], JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        SandboxLevel? sandbox = null;
        var sandboxRecognized = true;
        switch (parsed?.Sandbox?.ToUpperInvariant())
        {
            case null or "" or "NONE":
                break;
            case "STANDARD":
                sandbox = SandboxLevel.Standard;
                break;
            case "STRICT":
                sandbox = SandboxLevel.Strict;
                break;
            default:
                sandboxRecognized = false;
                break;
        }

        return parsed?.VerdictValue?.ToUpperInvariant() switch
        {
            "APPROVE" when sandboxRecognized => SupervisionDecision.Approve(sandbox),
            "DENY" => SupervisionDecision.Deny(parsed.Reason ?? "The AI supervisor denied this action."),
            _ => null,
        };
    }

    private sealed record Verdict
    {
        [System.Text.Json.Serialization.JsonPropertyName("verdict")]
        public string? VerdictValue { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("sandbox")]
        public string? Sandbox { get; init; }
    }
}
