namespace Orkis.Tools;

/// <summary>
/// A searchable catalogue of tools that are not declared to the model up front.
/// Progressive disclosure keeps large tool sets out of the context window: the agent
/// loop exposes a small always-on core plus a search meta-tool, the model queries the
/// catalogue when it needs a capability, and matches become active for the rest of
/// the run (recorded in run state, so they survive checkpoint and resume).
/// </summary>
public interface IToolCatalog
{
    /// <summary>
    /// How many tools the catalogue currently holds. Zero means there is nothing to
    /// disclose, so callers can omit the search meta-tool entirely.
    /// </summary>
    int Count { get; }

    /// <summary>Returns descriptors of catalogue tools matching the query, best first.</summary>
    Task<IReadOnlyList<ToolDescriptor>> SearchAsync(
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Resolves a catalogue tool by name, or <see langword="null"/> when no such tool
    /// exists (any longer) — callers treat that as a typed outcome, not an error.
    /// </summary>
    Task<ITool?> ResolveAsync(string name, CancellationToken cancellationToken = default);
}
