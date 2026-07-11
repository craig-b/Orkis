using Orkis.Tools;

namespace Orkis.Supervision;

/// <summary>
/// Auto-approves actions whose tool declares a risk at or below a threshold and
/// escalates everything else to an inner supervisor. Graduated supervision is built
/// by composing supervisors like this rather than by defining new modes.
/// </summary>
public sealed class ThresholdSupervisor(ToolRisk autoApproveUpTo, ISupervisor escalation) : ISupervisor
{
    /// <inheritdoc />
    public async Task<SupervisionDecision> ReviewAsync(
        ProposedAction action,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(action);

        return action.Tool.Risk <= autoApproveUpTo
            ? SupervisionDecision.Approve()
            : await escalation.ReviewAsync(action, cancellationToken).ConfigureAwait(false);
    }
}
