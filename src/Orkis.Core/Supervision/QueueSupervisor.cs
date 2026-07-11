namespace Orkis.Supervision;

/// <summary>
/// Defers every action it reviews to an <see cref="IApprovalInbox"/>: if a decision has
/// already been recorded it is returned, otherwise the action is submitted to the queue
/// and the run pauses until one arrives. This is the supervisor shape an out-of-process
/// approval UI needs — the approver does not have to be present, or even running, while
/// the run is. Compose behind a <see cref="ThresholdSupervisor"/> to auto-approve
/// low-risk tools and queue only the rest.
/// </summary>
public sealed class QueueSupervisor(IApprovalInbox queue, TimeProvider timeProvider) : ISupervisor
{
    /// <inheritdoc />
    public async Task<SupervisionDecision> ReviewAsync(
        ProposedAction action,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(action);

        var decision = await queue
            .GetDecisionAsync(action.RunId, action.Call.Id, cancellationToken)
            .ConfigureAwait(false);
        if (decision is not null)
        {
            return decision;
        }

        await queue
            .SubmitAsync(
                new PendingApproval
                {
                    RunId = action.RunId,
                    Call = action.Call,
                    Tool = action.Tool,
                    RequestedAt = timeProvider.GetUtcNow(),
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return SupervisionDecision.Defer();
    }
}
