namespace Orkis.Agents;

/// <summary>The lifecycle state of an agent run.</summary>
public enum RunStatus
{
    /// <summary>The run is actively executing.</summary>
    Running = 0,

    /// <summary>The run is paused, checkpointed, awaiting a supervision decision; resume to continue.</summary>
    AwaitingSupervision = 1,

    /// <summary>The run finished with a final model response.</summary>
    Completed = 2,

    /// <summary>The run stopped because a <see cref="Orkis.Runs.RunBudget"/> limit was reached.</summary>
    BudgetExceeded = 3,

    /// <summary>
    /// A conversational run finished its turn and awaits the next user message; the
    /// turn's end is not terminal.
    /// </summary>
    AwaitingUser = 4,
}
