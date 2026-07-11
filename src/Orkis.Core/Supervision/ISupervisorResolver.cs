namespace Orkis.Supervision;

/// <summary>
/// Resolves the supervisor governing a run from the run's supervisor key. The key is
/// part of the run's checkpointed state, so a resumed run reconnects to the same
/// supervision policy even in a different process.
/// </summary>
public interface ISupervisorResolver
{
    /// <summary>Returns the supervisor registered under <paramref name="key"/>.</summary>
    ISupervisor Resolve(string key);
}
