namespace Orkis.Supervision;

/// <summary>
/// Approves every proposed action unconditionally ("yolo mode"). Suitable for
/// development and for agents whose tools are all low-risk; not registered by
/// default — running unsupervised is an explicit choice.
/// </summary>
public sealed class AutoApproveSupervisor : ISupervisor
{
    /// <inheritdoc />
    public Task<SupervisionDecision> ReviewAsync(
        ProposedAction action,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(SupervisionDecision.Approve());
}
