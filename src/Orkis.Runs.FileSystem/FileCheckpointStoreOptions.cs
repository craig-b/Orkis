namespace Orkis.Runs;

/// <summary>Configuration for <see cref="FileCheckpointStore"/>.</summary>
public sealed class FileCheckpointStoreOptions
{
    /// <summary>
    /// Root directory for checkpoint storage; one subdirectory is created per run.
    /// Defaults to an "orkis/checkpoints" directory under the local application data path.
    /// </summary>
    public string RootPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis", "checkpoints");
}
