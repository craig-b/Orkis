using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orkis.Runs;

namespace Orkis.Client;

/// <summary>
/// Reads a Server-Sent Events stream of run events — the daemon's
/// <c>/v1/runs/{id}/events</c> wire format, where each <c>data:</c> line carries one
/// event's self-describing JSON. Unknown <c>$type</c> discriminators surface as
/// <see cref="UnknownRunEvent"/> rather than failing the stream, so an older client
/// degrades visibly against a newer daemon.
/// </summary>
public static class SseRunEvents
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Yields the events in <paramref name="stream"/> as they arrive.</summary>
    public static async IAsyncEnumerable<RunEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            // Our protocol writes one single-line data: field per event; other SSE
            // fields (id:, retry:, comments) carry nothing the payload lacks.
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            yield return Parse(line["data: ".Length..]);
        }
    }

    /// <summary>Parses one event's JSON, falling back to <see cref="UnknownRunEvent"/>.</summary>
    public static RunEvent Parse(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);

        try
        {
            var runEvent = JsonSerializer.Deserialize<RunEvent>(json, JsonOptions);
            if (runEvent is not null)
            {
                return runEvent;
            }
        }
        catch (NotSupportedException)
        {
            // Unknown $type discriminator.
        }
        catch (JsonException)
        {
            // Missing or malformed discriminator.
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new UnknownRunEvent
        {
            Type = root.TryGetProperty("$type", out var type) ? type.GetString() ?? "?" : "?",
            Payload = root.Clone(),
            RunId = root.TryGetProperty("runId", out var runId) ? runId.GetString() ?? "" : "",
            Sequence = root.TryGetProperty("sequence", out var sequence) ? sequence.GetInt64() : -1,
            Timestamp = root.TryGetProperty("timestamp", out var timestamp)
                ? timestamp.GetDateTimeOffset()
                : DateTimeOffset.MinValue,
        };
    }
}
