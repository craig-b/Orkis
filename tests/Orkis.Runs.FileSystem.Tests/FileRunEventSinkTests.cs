using Microsoft.Extensions.Options;
using Orkis.Runs;

namespace Orkis.Runs.FileSystem.Tests;

public sealed class FileRunEventSinkTests : IDisposable
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

    private FileRunEventSink CreateSink() => new(Options.Create(new FileRunEventSinkOptions { RootPath = _rootPath }));

    private static readonly DateTimeOffset At = new(2026, 7, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EventsRoundTripPolymorphicallyInOrder()
    {
        var sink = CreateSink();
        await sink.WriteAsync(
            new RunStartedEvent
            {
                RunId = "run-1",
                Sequence = 0,
                Timestamp = At,
                Prompt = "go",
                SupervisorKey = "default",
            }
        );
        await sink.WriteAsync(
            new ToolCallProposedEvent
            {
                RunId = "run-1",
                Sequence = 1,
                Timestamp = At,
                CallId = "c1",
                ToolName = "shell",
                ArgumentsJson = """{"command":"ls"}""",
            }
        );
        await sink.WriteAsync(
            new RunCompletedEvent
            {
                RunId = "run-1",
                Sequence = 2,
                Timestamp = At,
                Status = "Completed",
                InputTokens = 10,
                OutputTokens = 5,
                ToolCalls = 1,
            }
        );

        var events = await CreateSink().ReadAsync("run-1");

        Assert.Equal(3, events.Count);
        var started = Assert.IsType<RunStartedEvent>(events[0]);
        Assert.Equal("go", started.Prompt);
        var proposed = Assert.IsType<ToolCallProposedEvent>(events[1]);
        Assert.Equal("""{"command":"ls"}""", proposed.ArgumentsJson);
        var completed = Assert.IsType<RunCompletedEvent>(events[2]);
        Assert.Equal("Completed", completed.Status);
    }

    [Fact]
    public async Task ReplayFromASequenceSkipsEarlierEvents()
    {
        var sink = CreateSink();
        await sink.WriteAsync(
            new RunResumedEvent
            {
                RunId = "run-1",
                Sequence = 0,
                Timestamp = At,
            }
        );
        await sink.WriteAsync(
            new RunPausedEvent
            {
                RunId = "run-1",
                Sequence = 1,
                Timestamp = At,
            }
        );
        await sink.WriteAsync(
            new RunResumedEvent
            {
                RunId = "run-1",
                Sequence = 2,
                Timestamp = At,
            }
        );

        var events = await sink.ReadAsync("run-1", afterSequence: 0);

        Assert.Equal([1L, 2L], events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task UnknownRunsHaveEmptyHistories()
    {
        Assert.Empty(await CreateSink().ReadAsync("nope"));
    }

    [Fact]
    public async Task HostileRunIdsStayInsideTheRoot()
    {
        var sink = CreateSink();
        await sink.WriteAsync(
            new RunPausedEvent
            {
                RunId = "../escape",
                Sequence = 0,
                Timestamp = At,
            }
        );

        Assert.Single(await sink.ReadAsync("../escape"));
        var fullRoot = Path.GetFullPath(_rootPath);
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            Assert.StartsWith(fullRoot + Path.DirectorySeparatorChar, file, StringComparison.Ordinal);
        }
    }
}
