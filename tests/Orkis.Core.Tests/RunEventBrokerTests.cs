using Orkis.Runs;

namespace Orkis.Core.Tests;

public sealed class RunEventBrokerTests
{
    private static RunStartedEvent Event(string runId, long sequence) =>
        new()
        {
            RunId = runId,
            Sequence = sequence,
            Timestamp = DateTimeOffset.UnixEpoch,
            Prompt = "p",
            SupervisorKey = "queue",
        };

    [Fact]
    public async Task PerRunSubscriptionSeesOnlyItsRun()
    {
        var broker = new RunEventBroker();
        using var subscription = broker.Subscribe("run-a");

        await broker.WriteAsync(Event("run-a", 0));
        await broker.WriteAsync(Event("run-b", 0));
        await broker.WriteAsync(Event("run-a", 1));

        Assert.True(subscription.Reader.TryRead(out var first));
        Assert.Equal(0, first!.Sequence);
        Assert.True(subscription.Reader.TryRead(out var second));
        Assert.Equal(1, second!.Sequence);
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WildcardSubscriptionSeesEveryRun()
    {
        var broker = new RunEventBroker();
        using var subscription = broker.SubscribeAll();

        await broker.WriteAsync(Event("run-a", 0));
        await broker.WriteAsync(Event("run-b", 0));

        Assert.True(subscription.Reader.TryRead(out var first));
        Assert.Equal("run-a", first!.RunId);
        Assert.True(subscription.Reader.TryRead(out var second));
        Assert.Equal("run-b", second!.RunId);
    }

    [Fact]
    public async Task DisposedSubscriptionsStopReceiving()
    {
        var broker = new RunEventBroker();
        var subscription = broker.SubscribeAll();
        subscription.Dispose();

        await broker.WriteAsync(Event("run-a", 0));

        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ForwardsToTheInnerSinkBeforeFanOut()
    {
        var written = new List<RunEvent>();
        var broker = new RunEventBroker(new DelegateSink(written.Add));
        using var subscription = broker.SubscribeAll();

        await broker.WriteAsync(Event("run-a", 0));

        Assert.Single(written);
        Assert.True(subscription.Reader.TryRead(out _));
    }

    private sealed class DelegateSink(Action<RunEvent> onWrite) : IRunEventSink
    {
        public ValueTask WriteAsync(RunEvent runEvent, CancellationToken cancellationToken = default)
        {
            onWrite(runEvent);
            return ValueTask.CompletedTask;
        }
    }
}
