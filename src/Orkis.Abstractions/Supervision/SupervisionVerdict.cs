namespace Orkis.Supervision;

/// <summary>The outcome of reviewing a proposed action.</summary>
public enum SupervisionVerdict
{
    /// <summary>The action may proceed.</summary>
    Approved = 0,

    /// <summary>The action must not proceed; the agent is told why and continues.</summary>
    Denied = 1,

    /// <summary>
    /// No decision is available yet. The run checkpoints and pauses; it resumes
    /// when a decision arrives (e.g. from a human approver).
    /// </summary>
    Pending = 2,
}
