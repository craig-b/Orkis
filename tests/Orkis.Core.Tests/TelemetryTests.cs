using System.Diagnostics;
using System.Diagnostics.Metrics;
using Orkis.Agents;
using Orkis.Diagnostics;
using Orkis.Runs;
using Orkis.Supervision;

namespace Orkis.Core.Tests;

// Other test classes run in parallel and emit through the same process-wide
// instruments, so span assertions filter by run id and metric assertions are
// deliberately loose (>= rather than ==).
public sealed class TelemetryTests : IDisposable
{
    private readonly FakeChatClient _chatClient = new();
    private readonly FakeTool _tool = new();
    private readonly ScriptedSupervisor _supervisor = new();
    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;

    public TelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OrkisTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_activities)
                {
                    _activities.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _chatClient.Dispose();
    }

    private AgentRunner CreateRunner() =>
        new(
            _chatClient,
            [_tool],
            new FakeSupervisorResolver(_supervisor),
            new InMemoryCheckpointStore(),
            TimeProvider.System
        );

    [Fact]
    public async Task EmitsRunSupervisionAndToolSpans()
    {
        var runId = Guid.NewGuid().ToString("n");
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "fake_tool"));
        _chatClient.Enqueue(TestResponses.Text("done"));

        await CreateRunner().StartAsync(new AgentRunRequest { RunId = runId, Prompt = "go" });

        List<Activity> mine;
        lock (_activities)
        {
            mine = _activities.FindAll(a =>
                a.GetTagItem("orkis.run.id") as string == runId
                || a.Parent?.GetTagItem("orkis.run.id") as string == runId
            );
        }

        var runSpan = Assert.Single(mine, a => a.OperationName == OrkisTelemetry.RunActivityName);
        Assert.Equal("Completed", runSpan.GetTagItem("orkis.run.status"));
        Assert.Equal(20L, runSpan.GetTagItem("gen_ai.usage.input_tokens"));

        var reviewSpan = Assert.Single(mine, a => a.OperationName == OrkisTelemetry.SupervisionActivityName);
        Assert.Equal("Approved", reviewSpan.GetTagItem("orkis.supervision.verdict"));
        Assert.Equal("ReadOnly", reviewSpan.GetTagItem("orkis.tool.risk"));

        var toolSpan = Assert.Single(mine, a => a.OperationName == "execute_tool fake_tool");
        Assert.Equal("call-1", toolSpan.GetTagItem("gen_ai.tool.call.id"));
        Assert.NotEqual(ActivityStatusCode.Error, toolSpan.Status);
    }

    [Fact]
    public async Task RecordsTokenAndToolCallMetrics()
    {
        long tokens = 0;
        long executedToolCalls = 0;

        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OrkisTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
            {
                if (instrument.Name == OrkisTelemetry.TokensInstrumentName)
                {
                    Interlocked.Add(ref tokens, measurement);
                }
                else if (instrument.Name == OrkisTelemetry.ToolCallsInstrumentName)
                {
                    foreach (var tag in tags)
                    {
                        if (tag is { Key: "orkis.tool.outcome", Value: "executed" })
                        {
                            Interlocked.Add(ref executedToolCalls, measurement);
                        }
                    }
                }
            }
        );
        meterListener.Start();

        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "fake_tool"));
        _chatClient.Enqueue(TestResponses.Text("done"));

        await CreateRunner().StartAsync(new AgentRunRequest { Prompt = "go" });

        Assert.True(Interlocked.Read(ref tokens) >= 30, $"expected >= 30 tokens, saw {tokens}");
        Assert.True(Interlocked.Read(ref executedToolCalls) >= 1);
    }
}
