using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Orkis.Diagnostics;

/// <summary>Process-lifetime telemetry instruments shared by the agent runner.</summary>
internal static class OrkisInstrumentation
{
    public static readonly ActivitySource ActivitySource = new(OrkisTelemetry.ActivitySourceName);

    private static readonly Meter Meter = new(OrkisTelemetry.MeterName);

    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        OrkisTelemetry.TokensInstrumentName,
        unit: "{token}",
        description: "Tokens consumed by agent runs."
    );

    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>(
        OrkisTelemetry.ToolCallsInstrumentName,
        unit: "{call}",
        description: "Tool calls processed by agent runs, by outcome."
    );

    public static readonly Histogram<double> SegmentDuration = Meter.CreateHistogram<double>(
        OrkisTelemetry.SegmentDurationInstrumentName,
        unit: "s",
        description: "Duration of agent run segments."
    );
}
