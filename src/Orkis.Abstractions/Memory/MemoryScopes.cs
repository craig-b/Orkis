namespace Orkis.Memory;

/// <summary>Well-known memory scopes.</summary>
public static class MemoryScopes
{
    /// <summary>
    /// The shared scope every run searches in addition to its own. Memories that
    /// should follow a specific workload (a scheduled job, a project) belong in a
    /// scope named for it instead.
    /// </summary>
    public const string Global = "global";
}
