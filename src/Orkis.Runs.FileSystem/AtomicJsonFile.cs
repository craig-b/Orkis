using System.Text.Json;

namespace Orkis.Runs;

/// <summary>
/// JSON file writes that land in a temporary file and are moved into place, so a
/// crash mid-write never leaves a truncated file at the target path.
/// </summary>
internal static class AtomicJsonFile
{
    internal static async Task WriteAsync<T>(
        string finalPath,
        T value,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken
    )
    {
        var tempPath = finalPath + "." + Guid.NewGuid().ToString("n") + ".tmp";

        try
        {
            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous
            );
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    internal static async Task<T?> ReadAsync<T>(
        string path,
        JsonSerializerOptions? options,
        CancellationToken cancellationToken
    )
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous
        );
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
