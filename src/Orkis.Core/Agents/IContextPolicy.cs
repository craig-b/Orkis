using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;

namespace Orkis.Agents;

/// <summary>
/// Decides what enters the model's window. The run's transcript is never rewritten —
/// checkpoints, audit, and replay keep the full history — and the policy produces a
/// per-call view instead, persisting anything expensive (summaries) through the
/// cache so it survives checkpoint and resume without replacing what it summarizes.
/// </summary>
public interface IContextPolicy
{
    /// <summary>Produces the messages to send for the next model call.</summary>
    Task<ContextView> ComposeAsync(ContextRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What a context policy sees when composing a model call.</summary>
public sealed record ContextRequest
{
    /// <summary>
    /// The full transcript, in order. Append-only, so indexes are stable across the
    /// run — but this is a live view: read it during the compose call, do not hold it.
    /// </summary>
    public required IReadOnlyList<ChatMessage> Transcript { get; init; }

    /// <summary>
    /// Cache entries persisted in run state by earlier compositions (summaries keyed
    /// by the transcript range they cover).
    /// </summary>
    public required IReadOnlyDictionary<string, string> Cache { get; init; }

    /// <summary>
    /// Characters-per-token observed from the provider's reported usage so far, or
    /// <see langword="null"/> before the first model call of the run.
    /// </summary>
    public double? ObservedCharsPerToken { get; init; }
}

/// <summary>A context policy's composition result.</summary>
public sealed record ContextView
{
    /// <summary>The messages to send.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// New cache entries to persist in run state (and thus in checkpoints), so
    /// expensive work like summarization happens once per transcript range.
    /// </summary>
    public IReadOnlyDictionary<string, string> CacheUpdates { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
