using Microsoft.Extensions.Options;
using Orkis.Runs;
using Orkis.Scheduling;

namespace Orkis.Runs.FileSystem.Tests;

public sealed class FileScheduleStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("orkis-schedule-tests-").FullName;
    private readonly FileScheduleStore _store;

    public FileScheduleStoreTests() =>
        _store = new FileScheduleStore(Options.Create(new FileScheduleStoreOptions { RootPath = _root }));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Schedule Sample(string id) =>
        new()
        {
            Id = id,
            Name = "nightly",
            Cron = "0 3 * * *",
            Prompt = "review the day",
            Continuity = ScheduleContinuity.SharedStorageWithHandoff,
        };

    [Fact]
    public async Task RoundTripsAllFields()
    {
        var schedule = Sample("s1") with
        {
            ToolNames = ["search_corpus"],
            Handoff = "yesterday I found nothing",
            LastRunId = "run-9",
        };
        await _store.SaveAsync(schedule);

        var loaded = await _store.GetAsync("s1");

        Assert.NotNull(loaded);
        Assert.Equal(schedule with { ToolNames = null }, loaded with { ToolNames = null });
        Assert.Equal(schedule.ToolNames, loaded.ToolNames);
    }

    [Fact]
    public async Task ListsAndDeletes()
    {
        await _store.SaveAsync(Sample("s1"));
        await _store.SaveAsync(Sample("s2"));
        Assert.Equal(2, (await _store.ListAsync()).Count);

        await _store.DeleteAsync("s1");

        var remaining = Assert.Single(await _store.ListAsync());
        Assert.Equal("s2", remaining.Id);
        Assert.Null(await _store.GetAsync("s1"));
    }

    [Fact]
    public async Task SaveReplacesAndUnknownGetIsNull()
    {
        await _store.SaveAsync(Sample("s1"));
        await _store.SaveAsync(Sample("s1") with { Name = "renamed" });

        Assert.Equal("renamed", (await _store.GetAsync("s1"))!.Name);
        Assert.Null(await _store.GetAsync("absent"));
    }
}
