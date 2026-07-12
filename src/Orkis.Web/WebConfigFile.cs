using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orkis.Web;

/// <summary>
/// The gateway's slice of the shared daemon config file (see the daemon's
/// <c>OrkisConfig</c> for the format and full schema). Both processes read the same
/// JSONC file at the same path; unknown sections are ignored, so the daemon skips
/// <c>web</c> and the gateway skips <c>providers</c>/<c>models</c>. Kept self-contained
/// so the gateway stays free of project references.
/// </summary>
public sealed class WebConfigFile
{
    /// <summary>Root directory for daemon state; used to locate the token file and derive the socket.</summary>
    public string? DataDir { get; init; }

    /// <summary>The daemon's Unix socket the gateway proxies to.</summary>
    public string? Socket { get; init; }

    /// <summary>Gateway-specific settings.</summary>
    public WebSection? Web { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// The config file path: <c>ORKIS_CONFIG</c>, else <c>$XDG_CONFIG_HOME/orkis/config.json</c>,
    /// else <c>~/.config/orkis/config.json</c> — the same resolution the daemon uses.
    /// </summary>
    public static string DefaultPath()
    {
        if (Environment.GetEnvironmentVariable("ORKIS_CONFIG") is { Length: > 0 } explicitPath)
        {
            return explicitPath;
        }

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "orkis", "config.json");
    }

    /// <summary>Reads the config file, or <see langword="null"/> when none exists or it cannot be parsed.</summary>
    public static WebConfigFile? Load(string? path = null)
    {
        path ??= DefaultPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WebConfigFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            // A malformed file fails the daemon loudly; the gateway just falls back to
            // env vars and defaults rather than refusing to serve the UI.
            return null;
        }
    }

    /// <summary>The <c>web</c> section of the config file.</summary>
    public sealed class WebSection
    {
        /// <summary>TCP endpoint to listen on, e.g. <c>http://0.0.0.0:7420</c>.</summary>
        public string? Listen { get; init; }

        /// <summary>The bearer token inline. Omit to use the persisted token file (or a generated one).</summary>
        public string? Token { get; init; }

        /// <summary>Directory of built UI assets; omit for the ones beside the binary.</summary>
        public string? Assets { get; init; }

        /// <summary>Require the token on loopback too (normally exempt).</summary>
        public bool? RequireAuth { get; init; }
    }
}
