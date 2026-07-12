using System.Collections.Concurrent;
using Orkis.Agents;

namespace Orkis.Daemon;

/// <summary>
/// Executes runs in the background and tracks which are active in this process.
/// Terminal state lives in checkpoints, not here: a failed segment records its error
/// for the API to surface, but the run itself stays resumable from its checkpoint.
/// </summary>
internal sealed class RunExecutor
{
    private readonly RunnerFactory _runners;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ConcurrentDictionary<string, Task> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);

    public RunExecutor(RunnerFactory runners, IHostApplicationLifetime lifetime)
    {
        _runners = runners;
        _lifetime = lifetime;
    }

    /// <summary>Run ids currently executing in this process.</summary>
    public IReadOnlyCollection<string> ActiveRunIds => [.. _active.Keys];

    /// <summary>Whether the run is currently executing in this process.</summary>
    public bool IsActive(string runId) => _active.ContainsKey(runId);

    /// <summary>The error that ended the run's last segment in this process, if any.</summary>
    public string? FailureFor(string runId) => _failures.TryGetValue(runId, out var failure) ? failure : null;

    /// <summary>
    /// Starts the run in the background. Returns <see langword="false"/> when the run
    /// id is already executing.
    /// </summary>
    public bool TryStart(AgentRunRequest request)
    {
        var runner = _runners.Create(request.RunId, request.Conversational);
        return TryExecute(request.RunId, cancellationToken => runner.StartAsync(request, cancellationToken));
    }

    /// <summary>
    /// Resumes the run in the background under an explicit workspace and memory scope
    /// (see <see cref="RunnerFactory.ScopeForResume"/>), so the resumed run rebuilds
    /// the same tools it started with. Returns <see langword="false"/> when the run id
    /// is already executing.
    /// </summary>
    public bool TryResume(string runId, string workspaceKey, string memoryScope)
    {
        var runner = _runners.CreateForScope(workspaceKey, memoryScope);
        return TryExecute(runId, cancellationToken => runner.ResumeAsync(runId, cancellationToken));
    }

    /// <summary>
    /// Continues a chat with the next user message in the background. Returns
    /// <see langword="false"/> when the run id is already executing.
    /// </summary>
    public bool TryContinue(string runId, string userMessage)
    {
        var runner = _runners.Create(runId, conversational: true);
        return TryExecute(runId, cancellationToken => runner.ContinueAsync(runId, userMessage, cancellationToken));
    }

    private bool TryExecute(string runId, Func<CancellationToken, Task<AgentRunResult>> execute)
    {
        var started = new TaskCompletionSource();
        if (!_active.TryAdd(runId, started.Task))
        {
            return false;
        }

        _failures.TryRemove(runId, out _);
        _ = ExecuteAsync(runId, execute, started);
        return true;
    }

    private async Task ExecuteAsync(
        string runId,
        Func<CancellationToken, Task<AgentRunResult>> execute,
        TaskCompletionSource started
    )
    {
        try
        {
            // Daemon shutdown cancels the segment; the run stays resumable because the
            // loop checkpoints after every step.
            await execute(_lifetime.ApplicationStopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Shutdown, not failure.
        }
        catch (Exception ex)
        {
            _failures[runId] = ex.Message;
        }
        finally
        {
            _active.TryRemove(runId, out _);
            started.SetResult();
        }
    }
}
