namespace Orkis.Diagnostics;

/// <summary>
/// Names of the telemetry sources and signals Orkis emits, for wiring up
/// OpenTelemetry (<c>AddSource</c>/<c>AddMeter</c>) and building dashboards.
/// Tool spans follow the OpenTelemetry GenAI semantic conventions.
/// </summary>
public static class OrkisTelemetry
{
    /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> name for agent spans.</summary>
    public const string ActivitySourceName = "Orkis.Agents";

    /// <summary>The <see cref="System.Diagnostics.Metrics.Meter"/> name for agent metrics.</summary>
    public const string MeterName = "Orkis.Agents";

    /// <summary>Span emitted for each run segment (start or resume until completion or pause).</summary>
    public const string RunActivityName = "orkis.agent.run";

    /// <summary>Span emitted for each supervision review of a proposed tool call.</summary>
    public const string SupervisionActivityName = "orkis.supervision.review";

    /// <summary>Prefix of tool execution spans, per the GenAI conventions: "execute_tool {name}".</summary>
    public const string ExecuteToolActivityPrefix = "execute_tool";

    /// <summary>Counter of tokens consumed, tagged with <c>orkis.token.direction</c>.</summary>
    public const string TokensInstrumentName = "orkis.agent.tokens";

    /// <summary>Counter of tool calls processed, tagged with <c>orkis.tool.outcome</c>.</summary>
    public const string ToolCallsInstrumentName = "orkis.agent.tool_calls";

    /// <summary>Counter of model-call cost, in the currency the cost calculator is configured in.</summary>
    public const string CostInstrumentName = "orkis.agent.cost";

    /// <summary>Histogram of run segment durations in seconds, tagged with <c>orkis.run.status</c>.</summary>
    public const string SegmentDurationInstrumentName = "orkis.agent.run.segment.duration";
}
