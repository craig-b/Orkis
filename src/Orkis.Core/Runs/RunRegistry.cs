using System.Text.Json;
using Orkis.Agents;

namespace Orkis.Runs;

/// <summary>
/// Summarizes runs from their checkpoints. Derived, never authoritative: the
/// checkpoint store owns run state, so any host pointed at the same store sees the
/// same runs — including ones started by a process that no longer exists.
/// </summary>
public sealed class RunRegistry
{
    private readonly ICheckpointStore _checkpointStore;

    public RunRegistry(ICheckpointStore checkpointStore)
    {
        ArgumentNullException.ThrowIfNull(checkpointStore);
        _checkpointStore = checkpointStore;
    }

    /// <summary>All known runs, most recently checkpointed first.</summary>
    public async Task<IReadOnlyList<RunSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var checkpoints = await _checkpointStore.ListLatestAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. checkpoints
                .Select(Summarize)
                .OfType<RunSummary>()
                .OrderByDescending(static summary => summary.UpdatedAt),
        ];
    }

    /// <summary>The run's summary, or <see langword="null"/> when no checkpoint exists for it.</summary>
    public async Task<RunSummary?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var checkpoint = await _checkpointStore.LoadLatestAsync(runId, cancellationToken).ConfigureAwait(false);
        return checkpoint is null ? null : Summarize(checkpoint);
    }

    /// <summary>
    /// The run's conversation as of its latest checkpoint — text-bearing messages
    /// only (tool activity is the event stream's story) — or <see langword="null"/>
    /// when the run is unknown or its checkpoint is unreadable.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptMessage>?> GetTranscriptAsync(
        string runId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var checkpoint = await _checkpointStore.LoadLatestAsync(runId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return null;
        }

        Agents.AgentRunState? state;
        try
        {
            state = checkpoint.State.Deserialize<Agents.AgentRunState>(Agents.AgentRunner.StateJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (state is null)
        {
            return null;
        }

        return
        [
            .. state
                .Messages.Where(static message => !string.IsNullOrWhiteSpace(message.Text))
                .Select(static message => new TranscriptMessage { Role = message.Role.Value, Text = message.Text }),
        ];
    }

    private static RunSummary? Summarize(RunCheckpoint checkpoint)
    {
        AgentRunState? state;
        try
        {
            state = checkpoint.State.Deserialize<AgentRunState>(AgentRunner.StateJsonOptions);
        }
        catch (JsonException)
        {
            // A checkpoint this host cannot read (newer schema, corruption) hides the
            // run from summaries rather than failing the whole listing.
            return null;
        }

        if (state is null)
        {
            return null;
        }

        return new RunSummary
        {
            RunId = state.RunId,
            Status = state.Status,
            SupervisorKey = state.SupervisorKey,
            Conversational = state.Conversational,
            InputTokens = state.InputTokens,
            OutputTokens = state.OutputTokens,
            Cost = state.Cost,
            ToolCalls = state.ToolCallCount,
            UpdatedAt = checkpoint.CreatedAt,
        };
    }
}
