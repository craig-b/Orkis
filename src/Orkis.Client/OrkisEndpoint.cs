namespace Orkis.Client;

/// <summary>Resolves which daemon a client talks to. Instance identity is the endpoint.</summary>
public static class OrkisEndpoint
{
    /// <summary>
    /// The daemon endpoint to use: an explicit value wins (a socket path, or an
    /// <c>http(s)://</c> URL for a bearer-token TCP listener), then <c>ORKIS_HOST</c>
    /// (URL), then <c>ORKIS_SOCKET</c> (path), then the well-known default socket
    /// (<c>$XDG_RUNTIME_DIR/orkis/orkis.sock</c>, falling back to the local
    /// application data directory) — matching the daemon's own resolution.
    /// </summary>
    public static string Resolve(string? explicitEndpoint = null)
    {
        if (!string.IsNullOrEmpty(explicitEndpoint))
        {
            return explicitEndpoint;
        }

        if (Environment.GetEnvironmentVariable("ORKIS_HOST") is { Length: > 0 } host)
        {
            return host;
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

    /// <summary>Whether the endpoint is a TCP URL rather than a Unix socket path.</summary>
    public static bool IsHttp(string endpoint) =>
        endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
