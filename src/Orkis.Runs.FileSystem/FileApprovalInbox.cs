using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Orkis.Runs;

namespace Orkis.Supervision;

/// <summary>
/// Stores the approval inbox as JSON files under a root directory — one subdirectory
/// per run, one file per tool call, rewritten in place when the decision arrives — so
/// approvals can be granted from a different process than the paused run, and survive
/// both of them restarting. Files are indented with string enums, so the inbox is
/// directly readable (and in a pinch, decidable) with ordinary file tools.
/// </summary>
public sealed class FileApprovalInbox : IApprovalInbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _rootPath;

    public FileApprovalInbox(IOptions<FileApprovalInboxOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rootPath = Path.GetFullPath(options.Value.RootPath);
    }

    /// <summary>The on-disk shape: the request, plus the decision once one is recorded.</summary>
    private sealed record ApprovalRecord
    {
        public required PendingApproval Approval { get; init; }

        public SupervisionDecision? Decision { get; init; }
    }

    /// <inheritdoc />
    public async Task SubmitAsync(PendingApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);

        var path = EntryPath(approval.RunId, approval.Call.Id);
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicJsonFile
            .WriteAsync(path, new ApprovalRecord { Approval = approval }, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingApproval>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_rootPath))
        {
            return [];
        }

        var pending = new List<PendingApproval>();
        foreach (var path in Directory.EnumerateFiles(_rootPath, "*.json", SearchOption.AllDirectories))
        {
            var record = await AtomicJsonFile
                .ReadAsync<ApprovalRecord>(path, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (record is { Decision: null })
            {
                pending.Add(record.Approval);
            }
        }

        return [.. pending.OrderBy(static approval => approval.RequestedAt)];
    }

    /// <inheritdoc />
    public async Task<SupervisionDecision?> GetDecisionAsync(
        string runId,
        string callId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(callId);

        var path = EntryPath(runId, callId);
        if (!File.Exists(path))
        {
            return null;
        }

        var record = await AtomicJsonFile
            .ReadAsync<ApprovalRecord>(path, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return record?.Decision;
    }

    /// <inheritdoc />
    public async Task DecideAsync(
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

        var path = EntryPath(runId, callId);
        var record =
            (
                File.Exists(path)
                    ? await AtomicJsonFile
                        .ReadAsync<ApprovalRecord>(path, JsonOptions, cancellationToken)
                        .ConfigureAwait(false)
                    : null
            ) ?? throw new InvalidOperationException($"No approval found for run '{runId}', call '{callId}'.");

        if (record.Decision is not null)
        {
            throw new InvalidOperationException(
                $"The approval for run '{runId}', call '{callId}' was already decided."
            );
        }

        await AtomicJsonFile
            .WriteAsync(path, record with { Decision = decision }, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private string EntryPath(string runId, string callId) =>
        Path.Combine(_rootPath, SafePathNames.For(runId), SafePathNames.For(callId) + ".json");
}
