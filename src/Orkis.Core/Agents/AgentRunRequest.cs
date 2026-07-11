using Orkis.Runs;
using Orkis.Supervision;

namespace Orkis.Agents;

/// <summary>A request to start a new agent run.</summary>
public sealed record AgentRunRequest
{
    /// <summary>Identifier for the run; used to checkpoint and resume it. Defaults to a new id.</summary>
    public string RunId { get; init; } = Guid.CreateVersion7().ToString("n");

    /// <summary>The user prompt the agent should act on.</summary>
    public required string Prompt { get; init; }

    /// <summary>Optional system prompt establishing the agent's role and constraints.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Resource limits for the run.</summary>
    public RunBudget Budget { get; init; } = RunBudget.Unlimited;

    /// <summary>
    /// Key of the supervisor governing this run. Checkpointed with the run's state, so a
    /// resumed run reconnects to the same supervision policy.
    /// </summary>
    public string SupervisorKey { get; init; } = SupervisorKeys.Default;
}
