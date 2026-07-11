using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Orkis.Runs.FileSystem.Tests;

public sealed class FileCheckpointStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "orkis-tests",
        Guid.CreateVersion7().ToString("n")
    );

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private FileCheckpointStore CreateStore() =>
        new(Options.Create(new FileCheckpointStoreOptions { RootPath = _rootPath }));

    private static RunCheckpoint Checkpoint(string runId, long sequence, string stateJson = """{"step":0}""") =>
        new()
        {
            RunId = runId,
            Sequence = sequence,
            State = JsonSerializer.Deserialize<JsonElement>(stateJson),
            CreatedAt = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero).AddSeconds(sequence),
        };

    [Fact]
    public async Task LoadLatestReturnsNullWhenRunHasNoCheckpoints()
    {
        var store = CreateStore();

        Assert.Null(await store.LoadLatestAsync("missing-run"));
    }

    [Fact]
    public async Task SavedCheckpointRoundTripsAllFields()
    {
        var store = CreateStore();
        var checkpoint = Checkpoint("run-1", 3, """{"messages":["hi"],"cost":1.25,"nested":{"a":null}}""");

        await store.SaveAsync(checkpoint);
        var loaded = await store.LoadLatestAsync("run-1");

        Assert.NotNull(loaded);
        Assert.Equal(checkpoint.RunId, loaded.RunId);
        Assert.Equal(checkpoint.Sequence, loaded.Sequence);
        Assert.Equal(checkpoint.CreatedAt, loaded.CreatedAt);
        Assert.True(JsonElement.DeepEquals(checkpoint.State, loaded.State));
    }

    [Fact]
    public async Task LoadLatestReturnsHighestSequenceRegardlessOfSaveOrder()
    {
        var store = CreateStore();

        await store.SaveAsync(Checkpoint("run-1", 5));
        await store.SaveAsync(Checkpoint("run-1", 12));
        await store.SaveAsync(Checkpoint("run-1", 7));

        var loaded = await store.LoadLatestAsync("run-1");

        Assert.NotNull(loaded);
        Assert.Equal(12, loaded.Sequence);
    }

    [Fact]
    public async Task CheckpointsSurviveANewStoreInstanceOverTheSameRoot()
    {
        await CreateStore().SaveAsync(Checkpoint("run-1", 4, """{"survived":true}"""));

        var loaded = await CreateStore().LoadLatestAsync("run-1");

        Assert.NotNull(loaded);
        Assert.Equal(4, loaded.Sequence);
        Assert.True(loaded.State.GetProperty("survived").GetBoolean());
    }

    [Fact]
    public async Task RunsAreIsolatedFromEachOther()
    {
        var store = CreateStore();

        await store.SaveAsync(Checkpoint("run-a", 1));
        await store.SaveAsync(Checkpoint("run-b", 9));

        var loadedA = await store.LoadLatestAsync("run-a");

        Assert.NotNull(loadedA);
        Assert.Equal(1, loadedA.Sequence);
    }

    [Theory]
    [InlineData("../escape-attempt")]
    [InlineData("..\\windows\\escape")]
    [InlineData("nested/run/id")]
    [InlineData("spaces and:colons|pipes")]
    [InlineData(".")]
    public async Task HostileRunIdsStayInsideTheRootDirectory(string runId)
    {
        var store = CreateStore();

        await store.SaveAsync(Checkpoint(runId, 1));
        var loaded = await store.LoadLatestAsync(runId);

        Assert.NotNull(loaded);
        Assert.Equal(runId, loaded.RunId);

        var fullRoot = Path.GetFullPath(_rootPath);
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            Assert.StartsWith(fullRoot + Path.DirectorySeparatorChar, file, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunIdsThatSanitizeIdenticallyDoNotCollide()
    {
        var store = CreateStore();

        await store.SaveAsync(Checkpoint("run/1", 1));
        await store.SaveAsync(Checkpoint("run:1", 2));

        var first = await store.LoadLatestAsync("run/1");
        var second = await store.LoadLatestAsync("run:1");

        Assert.NotNull(first);
        Assert.Equal(1, first.Sequence);
        Assert.NotNull(second);
        Assert.Equal(2, second.Sequence);
    }

    [Fact]
    public async Task SavingTheSameSequenceTwiceKeepsTheLastWrite()
    {
        var store = CreateStore();

        await store.SaveAsync(Checkpoint("run-1", 1, """{"attempt":1}"""));
        await store.SaveAsync(Checkpoint("run-1", 1, """{"attempt":2}"""));

        var loaded = await store.LoadLatestAsync("run-1");

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.State.GetProperty("attempt").GetInt32());
    }

    [Fact]
    public async Task StrayFilesInTheRunDirectoryAreIgnored()
    {
        var store = CreateStore();
        await store.SaveAsync(Checkpoint("run-1", 2));

        var runDirectory = Directory.EnumerateDirectories(_rootPath).Single();
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "notes.json"), "not a checkpoint");
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "README.txt"), "hello");

        var loaded = await store.LoadLatestAsync("run-1");

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Sequence);
    }
}
