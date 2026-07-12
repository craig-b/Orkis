namespace Orkis.Runs;

/// <summary>Configuration for <see cref="FileRunEventSink"/>.</summary>
public sealed class FileRunEventSinkOptions
{
    /// <summary>
    /// Root directory for event logs; one JSON-lines file is created per run.
    /// Defaults to an "orkis/events" directory under the local application data path.
    /// </summary>
    public string RootPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis", "events");
}
