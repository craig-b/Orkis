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

    /// <summary>The conversation transcript so far.</summary>
    public List<ChatMessage> Messages { get; init; } = [];

    /// <summary>Tool calls requested by the model that have not yet been resolved.</summary>
    public List<ToolCall> PendingToolCalls { get; init; } = [];

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public int ToolCallCount { get; set; }

    /// <summary>Active running time accumulated across segments, excluding paused time.</summary>
    public TimeSpan ActiveDuration { get; set; }

    /// <summary>Sequence number the next checkpoint will use.</summary>
    public long NextSequence { get; set; }
}
