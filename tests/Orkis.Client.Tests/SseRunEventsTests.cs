using System.Text;
using Orkis.Client;
using Orkis.Runs;

namespace Orkis.Client.Tests;

public sealed class SseRunEventsTests
{
    [Fact]
    public void ParsesKnownEventTypes()
    {
        var runEvent = SseRunEvents.Parse(
            """
            {"$type":"run_started","prompt":"hi","supervisorKey":"queue","runId":"r1","sequence":0,"timestamp":"2026-07-12T10:00:00+00:00"}
            """
        );

        var started = Assert.IsType<RunStartedEvent>(runEvent);
        Assert.Equal("r1", started.RunId);
        Assert.Equal("hi", started.Prompt);
        Assert.Equal(0, started.Sequence);
    }

    [Fact]
    public void UnknownTypeFallsBackToUnknownRunEventInsteadOfThrowing()
    {
        var runEvent = SseRunEvents.Parse(
            """
            {"$type":"budget_warning","threshold":0.9,"runId":"r1","sequence":7,"timestamp":"2026-07-12T10:00:00+00:00"}
            """
        );

        var unknown = Assert.IsType<UnknownRunEvent>(runEvent);
        Assert.Equal("budget_warning", unknown.Type);
        Assert.Equal("r1", unknown.RunId);
        Assert.Equal(7, unknown.Sequence);
        Assert.Equal(0.9, unknown.Payload.GetProperty("threshold").GetDouble());
    }

    [Fact]
    public void MissingDiscriminatorFallsBackToUnknownRunEvent()
    {
        var runEvent = SseRunEvents.Parse("""{"runId":"r1","sequence":3}""");

        var unknown = Assert.IsType<UnknownRunEvent>(runEvent);
        Assert.Equal("?", unknown.Type);
        Assert.Equal(3, unknown.Sequence);
        Assert.Equal(DateTimeOffset.MinValue, unknown.Timestamp);
    }

    [Fact]
    public async Task ReadsAStreamMixingKnownAndUnknownEvents()
    {
        var sse =
            "id: 0\n"
            + """data: {"$type":"run_started","prompt":"p","supervisorKey":"queue","runId":"r1","sequence":0,"timestamp":"2026-07-12T10:00:00+00:00"}"""
            + "\n\n"
            + "id: 1\n"
            + """data: {"$type":"telepathy","runId":"r1","sequence":1,"timestamp":"2026-07-12T10:00:01+00:00"}"""
            + "\n\n"
            + "id: 2\n"
            + """data: {"$type":"run_completed","status":"Completed","inputTokens":1,"outputTokens":1,"cost":0,"toolCalls":0,"runId":"r1","sequence":2,"timestamp":"2026-07-12T10:00:02+00:00"}"""
            + "\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<RunEvent>();
        await foreach (var runEvent in SseRunEvents.ReadAsync(stream))
        {
            events.Add(runEvent);
        }

        Assert.Equal(3, events.Count);
        Assert.IsType<RunStartedEvent>(events[0]);
        var unknown = Assert.IsType<UnknownRunEvent>(events[1]);
        Assert.Equal("telepathy", unknown.Type);
        Assert.IsType<RunCompletedEvent>(events[2]);
    }
}
