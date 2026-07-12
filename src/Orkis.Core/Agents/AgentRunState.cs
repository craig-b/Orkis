using Microsoft.Extensions.AI;
using Orkis.Runs;
using Orkis.Tools;

namespace Orkis.Agents;

/// <summary>
/// The complete serializable state of an agent run. Persisted as a checkpoint
/// after every step so a run can resume after a crash, restart, or pause.
/// </summary>
internal sealed class AgentRunState
{
    public required string RunId { get; init; }

    public RunStatus Status { get; set; }

    public RunBudget Budget { get; init; } = RunBudget.Unlimited;

    /// <summary>Key of the supervisor governing this run.</summary>
    public string SupervisorKey { get; init; } = Orkis.Supervision.SupervisorKeys.Default;

    /// <summary>The conversation transcript so far.</summary>
    public List<ChatMessage> Messages { get; init; } = [];

    /// <summary>Tool calls requested by the model that have not yet been resolved.</summary>
    public List<ToolCall> PendingToolCalls { get; init; } = [];

    /// <summary>
    /// Names restricting the always-on tools for this run; empty means all registered
    /// tools are available.
    /// </summary>
    public List<string> CoreToolNames { get; init; } = [];

    /// <summary>
    /// Catalogue tools activated so far by <c>search_tools</c>. Re-resolved through the
    /// catalogue each segment, so activation survives checkpoint and resume.
    /// </summary>
    public List<string> ActiveToolNames { get; init; } = [];

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>Accumulated cost of the run's model calls, per the host's cost calculator.</summary>
    public decimal Cost { get; set; }

    /// <summary>Accumulated provider-specific token buckets (cache reads/writes, reasoning, …).</summary>
    public Dictionary<string, long> AdditionalTokenCounts { get; init; } = new(StringComparer.Ordinal);

    public int ToolCallCount { get; set; }

    /// <summary>Active running time accumulated across segments, excluding paused time.</summary>
    public TimeSpan ActiveDuration { get; set; }

    /// <summary>Sequence number the next checkpoint will use.</summary>
    public long NextSequence { get; set; }

    /// <summary>
    /// Cache written by the context policy (summaries keyed by transcript range), so
    /// compaction work survives checkpoint and resume. The transcript itself is never
    /// rewritten.
    /// </summary>
    public Dictionary<string, string> ContextCache { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Characters sent across model calls, for chars-per-token calibration.</summary>
    public long ObservedPromptChars { get; set; }

    /// <summary>Provider-reported input tokens across model calls, for calibration.</summary>
    public long ObservedPromptTokens { get; set; }
}
