namespace Orkis.Runs;

/// <summary>
/// One text-bearing message from a run's transcript. Tool activity is deliberately
/// absent — that story is told by the run's event stream; this is the conversation.
/// </summary>
public sealed record TranscriptMessage
{
    /// <summary>The message's role: system, user, or assistant.</summary>
    public required string Role { get; init; }

    /// <summary>The message's text content.</summary>
    public required string Text { get; init; }
}
