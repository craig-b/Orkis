using System.Text.Json.Serialization;

namespace Orkis.Runs;

/// <summary>
/// One thing that happened during a run. Events are the run's observable history —
/// streamed to UIs, appended to durable logs, and replayed for evals — where
/// checkpoints are its resumable state. Serialized polymorphically with a
/// <c>$type</c> discriminator, so logs and wire formats stay self-describing.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RunStartedEvent), "run_started")]
[JsonDerivedType(typeof(RunResumedEvent), "run_resumed")]
[JsonDerivedType(typeof(RunContinuedEvent), "run_continued")]
[JsonDerivedType(typeof(TurnCompletedEvent), "turn_completed")]
[JsonDerivedType(typeof(ModelCallCompletedEvent), "model_call_completed")]
[JsonDerivedType(typeof(ToolCallProposedEvent), "tool_call_proposed")]
[JsonDerivedType(typeof(SupervisionDecidedEvent), "supervision_decided")]
[JsonDerivedType(typeof(ToolCallCompletedEvent), "tool_call_completed")]
[JsonDerivedType(typeof(RunPausedEvent), "run_paused")]
[JsonDerivedType(typeof(RunCompletedEvent), "run_completed")]
public abstract record RunEvent
{
    /// <summary>The run this event belongs to.</summary>
    public required string RunId { get; init; }

    /// <summary>Monotonically increasing position within the run's event history.</summary>
    public required long Sequence { get; init; }

    /// <summary>When the event occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>A new run began.</summary>
public sealed record RunStartedEvent : RunEvent
{
    public required string Prompt { get; init; }

    public required string SupervisorKey { get; init; }

    /// <summary>The run's registered model key, or <see langword="null"/> for the host default.</summary>
    public string? ModelKey { get; init; }
}

/// <summary>A paused run picked up from its checkpoint.</summary>
public sealed record RunResumedEvent : RunEvent;

/// <summary>A chat received its next user message and is running another turn.</summary>
public sealed record RunContinuedEvent : RunEvent
{
    public required string Message { get; init; }
}

/// <summary>
/// A chat's turn finished with an assistant reply; the run awaits the next user
/// message. Usage figures are the run's running totals.
/// </summary>
public sealed record TurnCompletedEvent : RunEvent
{
    public required long InputTokens { get; init; }

    public required long OutputTokens { get; init; }

    public decimal Cost { get; init; }

    public required int ToolCalls { get; init; }

    public string? FinalTextPreview { get; init; }
}

/// <summary>A model call returned, with its usage and cost.</summary>
public sealed record ModelCallCompletedEvent : RunEvent
{
    public required long InputTokens { get; init; }

    public required long OutputTokens { get; init; }

    public decimal Cost { get; init; }

    public string? ModelId { get; init; }
}

/// <summary>The model asked for a tool call; supervision reviews it next.</summary>
public sealed record ToolCallProposedEvent : RunEvent
{
    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>The call's arguments as JSON text.</summary>
    public required string ArgumentsJson { get; init; }
}

/// <summary>A supervision decision on a proposed tool call, including any grants.</summary>
public sealed record SupervisionDecidedEvent : RunEvent
{
    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public required string Verdict { get; init; }

    public string? Reason { get; init; }

    public string? RequiredSandboxLevel { get; init; }

    public string? GrantedNetwork { get; init; }
}

/// <summary>A tool call finished executing (or was rejected before execution).</summary>
public sealed record ToolCallCompletedEvent : RunEvent
{
    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public required bool IsError { get; init; }

    /// <summary>The start of the result content; full content lives in the transcript.</summary>
    public required string ContentPreview { get; init; }

    public required int ContentLength { get; init; }
}

/// <summary>The run paused awaiting an out-of-band supervision decision.</summary>
public sealed record RunPausedEvent : RunEvent;

/// <summary>The run ended, in whatever status.</summary>
public sealed record RunCompletedEvent : RunEvent
{
    public required string Status { get; init; }

    public required long InputTokens { get; init; }

    public required long OutputTokens { get; init; }

    public decimal Cost { get; init; }

    public required int ToolCalls { get; init; }

    public string? FinalTextPreview { get; init; }
}
