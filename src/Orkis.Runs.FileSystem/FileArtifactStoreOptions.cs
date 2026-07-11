namespace Orkis.Artifacts;

/// <summary>Configuration for <see cref="FileArtifactStore"/>.</summary>
public sealed class FileArtifactStoreOptions
{
    /// <summary>
    /// Root directory for artifact storage; one subdirectory is created per artifact.
    /// Defaults to an "orkis/artifacts" directory under the local application data path.
    /// </summary>
    public string RootPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis", "artifacts");
}
