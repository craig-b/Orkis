using System.Text.Json;
using Orkis.Runs;

namespace Orkis.Client;

/// <summary>
/// A run event whose <c>$type</c> this client does not know — a newer daemon speaking
/// a newer protocol. The stream stays lossless: consumers see that something happened
/// (and its raw payload) instead of a silently dropped event.
/// </summary>
public sealed record UnknownRunEvent : RunEvent
{
    /// <summary>The wire discriminator this client did not recognize.</summary>
    public required string Type { get; init; }

    /// <summary>The event's raw JSON payload.</summary>
    public required JsonElement Payload { get; init; }
}
