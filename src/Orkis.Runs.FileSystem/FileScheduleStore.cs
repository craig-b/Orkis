using System.Text.Json;
using Microsoft.Extensions.Options;
using Orkis.Scheduling;

namespace Orkis.Runs;

/// <summary>
/// Stores schedules as JSON files under a root directory — one file per schedule,
/// written atomically — so they survive a process restart and any daemon pointed at
/// the same directory adopts them.
/// </summary>
public sealed class FileScheduleStore : IScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _rootPath;

    public FileScheduleStore(IOptions<FileScheduleStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rootPath = Path.GetFullPath(options.Value.RootPath);
    }

    /// <inheritdoc />
    public async Task SaveAsync(Schedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        Directory.CreateDirectory(_rootPath);
        await AtomicJsonFile
            .WriteAsync(PathFor(schedule.Id), schedule, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Schedule?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var path = PathFor(id);
        return File.Exists(path)
            ? await AtomicJsonFile.ReadAsync<Schedule>(path, JsonOptions, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_rootPath))
        {
            return [];
        }

        var schedules = new List<Schedule>();
        foreach (var path in Directory.EnumerateFiles(_rootPath, "*.json"))
        {
            var schedule = await AtomicJsonFile
                .ReadAsync<Schedule>(path, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (schedule is not null)
            {
                schedules.Add(schedule);
            }
        }

        return schedules;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string id) => Path.Combine(_rootPath, SafePathNames.For(id) + ".json");
}
