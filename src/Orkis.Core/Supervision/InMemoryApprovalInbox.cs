using System.Collections.Concurrent;

namespace Orkis.Supervision;

/// <summary>
/// Keeps pending approvals and their decisions in process memory. Suitable for
/// development and testing; entries do not survive a process restart.
/// </summary>
public sealed class InMemoryApprovalInbox : IApprovalInbox
{
    private sealed record Entry(PendingApproval Approval, SupervisionDecision? Decision);

    private readonly ConcurrentDictionary<(string RunId, string CallId), Entry> _entries = new();

    /// <inheritdoc />
    public Task SubmitAsync(PendingApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);

        _entries.TryAdd((approval.RunId, approval.Call.Id), new Entry(approval, Decision: null));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PendingApproval>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PendingApproval>>([
            .. _entries
                .Values.Where(static entry => entry.Decision is null)
                .Select(static entry => entry.Approval)
                .OrderBy(static approval => approval.RequestedAt),
        ]);

    /// <inheritdoc />
    public Task<SupervisionDecision?> GetDecisionAsync(
        string runId,
        string callId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(callId);

        return Task.FromResult(_entries.TryGetValue((runId, callId), out var entry) ? entry.Decision : null);
    }

    /// <inheritdoc />
    public Task DecideAsync(
        string runId,
        string callId,
        SupervisionDecision decision,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Verdict == SupervisionVerdict.Pending)
        {
            throw new ArgumentException("A recorded decision cannot itself be Pending.", nameof(decision));
        }

        while (true)
        {
            if (!_entries.TryGetValue((runId, callId), out var existing))
            {
                throw new InvalidOperationException($"No approval found for run '{runId}', call '{callId}'.");
            }

            if (existing.Decision is not null)
            {
                throw new InvalidOperationException(
                    $"The approval for run '{runId}', call '{callId}' was already decided."
                );
            }

            if (_entries.TryUpdate((runId, callId), existing with { Decision = decision }, existing))
            {
                return Task.CompletedTask;
            }
        }
    }
}
