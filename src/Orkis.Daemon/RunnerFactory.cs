using Microsoft.Extensions.AI;
using Orkis.Agents;
using Orkis.Artifacts;
using Orkis.Clients;
using Orkis.Memory;
using Orkis.Retrieval;
using Orkis.Runs;
using Orkis.Sandboxing;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Daemon;

/// <summary>
/// Builds an <see cref="AgentRunner"/> per run, so storage-bearing tools can be
/// scoped to the run: a chat gets its own workspace and memory scope
/// (<c>chat-&lt;runId&gt;</c>) — a durable working context, not just message history —
/// while one-shot runs share the daemon's default workspace and global memory.
/// Workspace keys are deterministic from the run id, so resuming (in any process)
/// reconstructs the same scope.
/// </summary>
internal sealed class RunnerFactory(IServiceProvider provider, DaemonSettings settings)
{
    /// <summary>The always-on tool names, for capability enumeration.</summary>
    public IReadOnlyList<string> ToolNames { get; } =
    [.. BuildTools(provider, settings.WorkspaceKey, MemoryScopes.Global).Select(t => t.Descriptor.Name)];

    /// <summary>The workspace a run's storage-bearing tools operate in.</summary>
    public string WorkspaceKeyFor(string runId, bool conversational) =>
        conversational ? $"chat-{runId}" : settings.WorkspaceKey;

    /// <summary>The scope a run's memories are saved to and recalled from.</summary>
    public static string MemoryScopeFor(string runId, bool conversational) =>
        conversational ? $"chat-{runId}" : MemoryScopes.Global;

    public AgentRunner Create(string runId, bool conversational) =>
        new(
            provider.GetRequiredService<IChatClient>(),
            BuildTools(provider, WorkspaceKeyFor(runId, conversational), MemoryScopeFor(runId, conversational)),
            provider.GetRequiredService<ISupervisorResolver>(),
            provider.GetRequiredService<ICheckpointStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ICostCalculator>(),
            provider.GetService<IToolCatalog>(),
            provider.GetService<IMemoryStore>(),
            provider.GetService<IContextPolicy>(),
            provider.GetService<IRunEventSink>(),
            provider.GetService<IChatClientResolver>()
        );

    private static List<ITool> BuildTools(IServiceProvider provider, string workspaceKey, string memoryScope)
    {
        var sandboxes = provider.GetServices<ISandbox>().ToList();
        var artifacts = provider.GetRequiredService<IArtifactStore>();
        var tools = new List<ITool>
        {
            new ShellTool(sandboxes, workspaceKey),
            new PromoteArtifactTool(sandboxes, artifacts, workspaceKey),
            new StageArtifactTool(sandboxes, artifacts, workspaceKey),
        };

        if (provider.GetService<IMemoryStore>() is { } memoryStore)
        {
            var timeProvider = provider.GetRequiredService<TimeProvider>();
            tools.Add(new SaveMemoryTool(memoryStore, memoryScope, timeProvider));
            tools.Add(new SearchMemoriesTool(memoryStore, memoryScope));
        }

        if (provider.GetService<IRetriever>() is { } retriever)
        {
            tools.Add(new RetrievalTool(retriever, provider.GetService<IReranker>()));
        }

        return tools;
    }
}
