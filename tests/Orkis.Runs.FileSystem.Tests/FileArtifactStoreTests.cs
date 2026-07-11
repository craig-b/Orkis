using System.Text;
using Microsoft.Extensions.Options;
using Orkis.Artifacts;

namespace Orkis.Runs.FileSystem.Tests;

public sealed class FileArtifactStoreTests : IDisposable
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

    private FileArtifactStore CreateStore() =>
        new(Options.Create(new FileArtifactStoreOptions { RootPath = _rootPath }));

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task SavedArtifactRoundTripsContentAndMetadata()
    {
        var store = CreateStore();

        var info = await store.SaveAsync("report.txt", Content("the findings"));

        Assert.Equal("report.txt", info.Name);
        Assert.Equal("the findings".Length, info.Length);

        var stream = await store.OpenAsync("report.txt");
        Assert.NotNull(stream);
        await using (stream)
        {
            using var reader = new StreamReader(stream);
            Assert.Equal("the findings", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task ArtifactsAreImmutable()
    {
        var store = CreateStore();
        await store.SaveAsync("report.txt", Content("v1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync("report.txt", Content("v2")));

        var stream = await store.OpenAsync("report.txt");
        Assert.NotNull(stream);
        await using (stream)
        {
            using var reader = new StreamReader(stream);
            Assert.Equal("v1", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task OpenReturnsNullForUnknownArtifacts()
    {
        Assert.Null(await CreateStore().OpenAsync("missing"));
    }

    [Fact]
    public async Task ListReturnsArtifactsOldestFirstAcrossInstances()
    {
        await CreateStore().SaveAsync("first", Content("a"));
        await CreateStore().SaveAsync("second", Content("bb"));

        var artifacts = await CreateStore().ListAsync();

        Assert.Equal(["first", "second"], artifacts.Select(a => a.Name));
        Assert.Equal([1L, 2L], artifacts.Select(a => a.Length));
    }

    [Fact]
    public async Task ListIsEmptyForAFreshStore()
    {
        Assert.Empty(await CreateStore().ListAsync());
    }

    [Fact]
    public async Task HostileArtifactNamesStayInsideTheRootDirectory()
    {
        var store = CreateStore();
        await store.SaveAsync("../../etc/escape", Content("contained"));

        var stream = await store.OpenAsync("../../etc/escape");
        Assert.NotNull(stream);
        await stream.DisposeAsync();

        var fullRoot = Path.GetFullPath(_rootPath);
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            Assert.StartsWith(fullRoot + Path.DirectorySeparatorChar, file, StringComparison.Ordinal);
        }
    }
}
