using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Orkis.Runs;

/// <summary>
/// Appends each run's events to a JSON-lines file — one line per event, one file per
/// run — and reads them back for replay. The log is the run's durable observable
/// history: what UIs stream, and what future evals replay.
/// </summary>
public sealed class FileRunEventSink : IRunEventSink, IRunEventLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _rootPath;

    public FileRunEventSink(IOptions<FileRunEventSinkOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rootPath = Path.GetFullPath(options.Value.RootPath);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(RunEvent runEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runEvent);

        Directory.CreateDirectory(_rootPath);
        var line = JsonSerializer.Serialize(runEvent, JsonOptions) + "\n";
        var stream = new FileStream(
            LogPath(runEvent.RunId),
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous
        );
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunEvent>> ReadAsync(
        string runId,
        long afterSequence = -1,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var path = LogPath(runId);
        if (!File.Exists(path))
        {
            return [];
        }

        var events = new List<RunEvent>();
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.Asynchronous
        );
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var runEvent = JsonSerializer.Deserialize<RunEvent>(line, JsonOptions);
                if (runEvent is not null && runEvent.Sequence > afterSequence)
                {
                    events.Add(runEvent);
                }
            }
        }

        return events;
    }

    private string LogPath(string runId) => Path.Combine(_rootPath, SafePathNames.For(runId) + ".jsonl");
}
