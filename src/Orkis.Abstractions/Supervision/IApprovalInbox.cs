namespace Orkis.Supervision;

/// <summary>
/// An inbox for supervision decisions made out of band: a supervisor submits proposed
/// actions and returns <see cref="SupervisionVerdict.Pending"/>, the run checkpoints
/// and pauses, and an approver decides later — from a console, a web UI, or another
/// process entirely. Entries are identified by run id plus tool call id.
/// </summary>
public interface IApprovalInbox
{
    /// <summary>
    /// Records an approval request. Submitting an already-known run/call pair is a
    /// no-op, so re-reviews after a resume neither duplicate nor reset entries.
    /// </summary>
    Task SubmitAsync(PendingApproval approval, CancellationToken cancellationToken = default);

    /// <summary>All undecided approvals, oldest first.</summary>
    Task<IReadOnlyList<PendingApproval>> ListPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The decision recorded for the given call, or <see langword="null"/> while it is
    /// still pending or was never submitted.
    /// </summary>
    Task<SupervisionDecision?> GetDecisionAsync(
        string runId,
        string callId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Records the decision for a pending approval. Decisions are immutable once made:
    /// deciding an unknown or already-decided entry throws
    /// <see cref="InvalidOperationException"/>, and the decision itself must not be
    /// <see cref="SupervisionVerdict.Pending"/>.
    /// </summary>
    Task DecideAsync(
        string runId,
        string callId,
        SupervisionDecision decision,
        CancellationToken cancellationToken = default
    );
}
