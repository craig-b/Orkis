namespace Orkis.Supervision;

/// <summary>
/// Reviews proposed agent actions before they execute. Implementations range from
/// a human approval queue to an AI supervisor, a rules engine, or unconditional
/// approval ("yolo mode") — all behind the same contract.
/// </summary>
public interface ISupervisor
{
    /// <summary>Reviews the proposed action and decides whether and how it may execute.</summary>
    Task<SupervisionDecision> ReviewAsync(ProposedAction action, CancellationToken cancellationToken = default);
}
