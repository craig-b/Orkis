namespace Orkis.Scheduling;

/// <summary>
/// Durable storage for schedules. Like the checkpoint store, this is the authoritative
/// state a daemon adopts on restart — schedules survive the process that created them.
/// </summary>
public interface IScheduleStore
{
    /// <summary>Creates or replaces a schedule.</summary>
    Task SaveAsync(Schedule schedule, CancellationToken cancellationToken = default);

    /// <summary>The schedule with this id, or <see langword="null"/> if none exists.</summary>
    Task<Schedule?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>All schedules, in no particular order.</summary>
    Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes a schedule. Removing an unknown id is a no-op.</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
