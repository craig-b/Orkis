namespace Orkis.Web;

/// <summary>
/// Configuration for one gateway instance. The gateway owns everything network the
/// daemon deliberately does not: the TCP bind, auth, and the UI assets. <c>Program</c>
/// builds this from environment variables; tests construct it directly.
/// </summary>
public sealed record WebSettings
{
    /// <summary>The TCP endpoint to listen on, e.g. <c>http://127.0.0.1:7420</c>.</summary>
    public required string ListenUrl { get; init; }

    /// <summary>The daemon's Unix socket that <c>/v1/*</c> is proxied to.</summary>
    public required string DaemonSocketPath { get; init; }

    /// <summary>
    /// The root credential: required (as a bearer header, or exchanged for a cookie
    /// session at <c>/auth/session</c>) on every non-loopback request.
    /// </summary>
    public required string BearerToken { get; init; }

    /// <summary>
    /// Requires auth on loopback connections too — normally exempt, the way the
    /// daemon's socket is trusted by file permissions.
    /// </summary>
    public bool RequireAuthOnLoopback { get; init; }

    /// <summary>Directory of built UI assets, or <see langword="null"/> for a placeholder page.</summary>
    public string? AssetsPath { get; init; }
}
