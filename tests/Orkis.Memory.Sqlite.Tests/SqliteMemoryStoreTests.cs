using Microsoft.Extensions.Options;

namespace Orkis.Memory.Sqlite.Tests;

public sealed class SqliteMemoryStoreTests : IDisposable
{
    private readonly FakeEmbeddingGenerator _embeddings = new();
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "orkis-tests",
        $"memory-{Guid.NewGuid():n}.db"
    );

    public void Dispose()
    {
        _embeddings.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private SqliteMemoryStore CreateStore() =>
        new(_embeddings, Options.Create(new SqliteMemoryStoreOptions { DatabasePath = _databasePath }));

    private static MemoryEntry Entry(string id, string text, string scope = MemoryScopes.Global) =>
        new()
        {
            Id = id,
            Text = text,
            Scope = scope,
            CreatedAt = new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.Zero),
        };

    [Fact]
    public async Task SearchReturnsMostRelevantFirst()
    {
        var store = CreateStore();
        await store.WriteAsync(Entry("1", "The user prefers tabs over spaces."));
        await store.WriteAsync(Entry("2", "The deploy pipeline runs at midnight."));

        var results = await store.SearchAsync("what are the user preferences about tabs", topK: 1);

        Assert.Equal("1", Assert.Single(results).Item.Id);
    }

    [Fact]
    public async Task ScopedSearchSeesItsScopePlusGlobalButNotOthers()
    {
        var store = CreateStore();
        await store.WriteAsync(Entry("g", "Shared fact about deploy tabs."));
        await store.WriteAsync(Entry("mine", "Cron fact about deploy tabs.", scope: "cron-report"));
        await store.WriteAsync(Entry("other", "Other fact about deploy tabs.", scope: "another-job"));

        var results = await store.SearchAsync("deploy tabs", scope: "cron-report", topK: 10);

        Assert.Equal(["g", "mine"], results.Select(r => r.Item.Id).Order());
    }

    [Fact]
    public async Task WriteReplacesById()
    {
        var store = CreateStore();
        await store.WriteAsync(Entry("1", "Original note about tabs."));
        await store.WriteAsync(Entry("1", "Corrected note about tabs."));

        var results = await store.SearchAsync("tabs", topK: 10);

        Assert.Contains("Corrected", Assert.Single(results).Item.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRemovesTheEntry()
    {
        var store = CreateStore();
        await store.WriteAsync(Entry("1", "A note about tabs."));

        await store.DeleteAsync("1");

        Assert.Empty(await store.SearchAsync("tabs", topK: 10));
    }

    [Fact]
    public async Task MemoriesSurviveANewStoreInstance()
    {
        await CreateStore().WriteAsync(Entry("1", "The user prefers tabs.", scope: "project-x"));

        var results = await CreateStore().SearchAsync("tabs", scope: "project-x");

        var single = Assert.Single(results);
        Assert.Equal("project-x", single.Item.Scope);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.Zero), single.Item.CreatedAt);
    }
}
