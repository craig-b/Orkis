namespace Orkis.Artifacts;

/// <summary>Metadata for a stored artifact.</summary>
public sealed record ArtifactInfo
{
    /// <summary>The artifact's unique name within the store.</summary>
    public required string Name { get; init; }

    /// <summary>Content length in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>When the artifact was stored.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
