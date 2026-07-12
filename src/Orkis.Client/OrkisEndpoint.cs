namespace Orkis.Client;

/// <summary>Resolves which daemon a client talks to. Instance identity is the endpoint.</summary>
public static class OrkisEndpoint
{
    /// <summary>
    /// The daemon socket to use: an explicit path wins, then <c>ORKIS_SOCKET</c>, then
    /// the well-known default (<c>$XDG_RUNTIME_DIR/orkis/orkis.sock</c>, falling back
    /// to the local application data directory) — matching the daemon's own resolution.
    /// </summary>
    public static string ResolveSocketPath(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        if (Environment.GetEnvironmentVariable("ORKIS_SOCKET") is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var baseDir = string.IsNullOrEmpty(runtimeDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis")
            : Path.Combine(runtimeDir, "orkis");
        return Path.Combine(baseDir, "orkis.sock");
    }
}
