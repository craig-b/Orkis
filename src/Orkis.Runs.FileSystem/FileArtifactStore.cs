using Microsoft.Extensions.Options;
using Orkis.Runs;

namespace Orkis.Artifacts;

/// <summary>
/// Stores artifacts as files under a root directory — one subdirectory per artifact
/// holding its content and a metadata file. The metadata file is written last and
/// atomically, so an artifact exists exactly when its metadata does and a crash
/// mid-save never leaves a half-visible artifact.
/// </summary>
public sealed class FileArtifactStore : IArtifactStore
{
    private const string MetadataFileName = "artifact.json";
    private const string ContentFileName = "content.bin";

    private readonly string _rootPath;
    private readonly TimeProvider _timeProvider;

    public FileArtifactStore(IOptions<FileArtifactStoreOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _rootPath = Path.GetFullPath(options.Value.RootPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ArtifactInfo> SaveAsync(
        string name,
        Stream content,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(content);

        var artifactDirectory = GetArtifactDirectory(name);
        var metadataPath = Path.Combine(artifactDirectory, MetadataFileName);
        if (File.Exists(metadataPath))
        {
            throw new InvalidOperationException(
                $"Artifact '{name}' already exists; artifacts are immutable — choose another name."
            );
        }

        Directory.CreateDirectory(artifactDirectory);
        var contentPath = Path.Combine(artifactDirectory, ContentFileName);
        var tempPath = contentPath + "." + Guid.NewGuid().ToString("n") + ".tmp";
        long length;
        try
        {
            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous
            );
            await using (stream.ConfigureAwait(false))
            {
                await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                length = stream.Length;
            }

            File.Move(tempPath, contentPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }

        var info = new ArtifactInfo
        {
            Name = name,
            Length = length,
            CreatedAt = _timeProvider.GetUtcNow(),
        };
        await AtomicJsonFile.WriteAsync(metadataPath, info, options: null, cancellationToken).ConfigureAwait(false);
        return info;
    }

    /// <inheritdoc />
    public Task<Stream?> OpenAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var artifactDirectory = GetArtifactDirectory(name);
        if (!File.Exists(Path.Combine(artifactDirectory, MetadataFileName)))
        {
            return Task.FromResult<Stream?>(null);
        }

        return Task.FromResult<Stream?>(
            new FileStream(
                Path.Combine(artifactDirectory, ContentFileName),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous
            )
        );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtifactInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_rootPath))
        {
            return [];
        }

        var artifacts = new List<ArtifactInfo>();
        foreach (var directory in Directory.EnumerateDirectories(_rootPath))
        {
            var metadataPath = Path.Combine(directory, MetadataFileName);
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            var info = await AtomicJsonFile
                .ReadAsync<ArtifactInfo>(metadataPath, options: null, cancellationToken)
                .ConfigureAwait(false);
            if (info is not null)
            {
                artifacts.Add(info);
            }
        }

        return [.. artifacts.OrderBy(static artifact => artifact.CreatedAt)];
    }

    private string GetArtifactDirectory(string name) => Path.Combine(_rootPath, SafePathNames.For(name));
}
