using System.Threading.Channels;

namespace Orkis.Runs;

/// <summary>
/// Fans run events out to live subscribers after forwarding them to an inner durable
/// sink. The durable write happens first and is the only one the runner awaits;
/// subscriber channels are unbounded and never block or fail the run, matching
/// <see cref="IRunEventSink"/>'s isolation guidance. A subscriber that needs a gapless
/// history subscribes first, then replays the log and skips duplicates by
/// <see cref="RunEvent.Sequence"/>.
/// </summary>
public sealed class RunEventBroker : IRunEventSink
{
    private readonly IRunEventSink? _inner;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<RunEventSubscription>> _subscriptions = new(StringComparer.Ordinal);

    /// <summary>Creates a broker forwarding to <paramref name="inner"/> before fan-out, if given.</summary>
    public RunEventBroker(IRunEventSink? inner = null) => _inner = inner;

    /// <inheritdoc />
    public async ValueTask WriteAsync(RunEvent runEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runEvent);

        if (_inner is not null)
        {
            await _inner.WriteAsync(runEvent, cancellationToken).ConfigureAwait(false);
        }

        RunEventSubscription[] targets;
        lock (_lock)
        {
            targets = _subscriptions.TryGetValue(runEvent.RunId, out var list) ? [.. list] : [];
        }

        foreach (var subscription in targets)
        {
            subscription.Publish(runEvent);
        }
    }

    /// <summary>
    /// Subscribes to the run's future events. Dispose to unsubscribe; disposal completes
    /// the subscription's reader.
    /// </summary>
    public RunEventSubscription Subscribe(string runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var subscription = new RunEventSubscription(runId, this);
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue(runId, out var list))
            {
                list = [];
                _subscriptions[runId] = list;
            }

            list.Add(subscription);
        }

        return subscription;
    }

    private void Unsubscribe(RunEventSubscription subscription)
    {
        lock (_lock)
        {
            if (_subscriptions.TryGetValue(subscription.RunId, out var list))
            {
                list.Remove(subscription);
                if (list.Count == 0)
                {
                    _subscriptions.Remove(subscription.RunId);
                }
            }
        }
    }

    /// <summary>A live subscription to one run's events.</summary>
    public sealed class RunEventSubscription : IDisposable
    {
        private readonly RunEventBroker _broker;
        private readonly Channel<RunEvent> _channel = Channel.CreateUnbounded<RunEvent>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = true }
        );

        internal RunEventSubscription(string runId, RunEventBroker broker)
        {
            RunId = runId;
            _broker = broker;
        }

        /// <summary>The subscribed run.</summary>
        public string RunId { get; }

        /// <summary>Events published since the subscription was created.</summary>
        public ChannelReader<RunEvent> Reader => _channel.Reader;

        internal void Publish(RunEvent runEvent) => _channel.Writer.TryWrite(runEvent);

        public void Dispose()
        {
            _broker.Unsubscribe(this);
            _channel.Writer.TryComplete();
        }
    }
}
