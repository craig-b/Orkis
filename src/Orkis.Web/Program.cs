using Orkis.Web;

// The gateway is the daemon's network face: it owns the TCP bind, the token, and the
// UI assets, and proxies /v1/* over the daemon's Unix socket. Loopback is exempt from
// auth by default (like the socket, trust is local); remote requests need the token.
var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "orkis");
var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
var daemonSocket =
    Environment.GetEnvironmentVariable("ORKIS_SOCKET")
    ?? Path.Combine(string.IsNullOrEmpty(runtimeDir) ? dataRoot : Path.Combine(runtimeDir, "orkis"), "orkis.sock");

var token = Environment.GetEnvironmentVariable("ORKIS_TOKEN");
if (string.IsNullOrEmpty(token))
{
    var tokenPath = Path.Combine(dataRoot, "token");
    if (File.Exists(tokenPath))
    {
        token = (await File.ReadAllTextAsync(tokenPath)).Trim();
    }
    else
    {
        token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(tokenPath, token + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    Console.WriteLine($"token: persisted at {tokenPath} (set ORKIS_TOKEN to override)");
}

// Built UI assets live next to the binary when published; ORKIS_WEB_ASSETS overrides.
var assetsPath = Environment.GetEnvironmentVariable("ORKIS_WEB_ASSETS");
if (string.IsNullOrEmpty(assetsPath))
{
    var defaultAssets = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    assetsPath = Directory.Exists(defaultAssets) ? defaultAssets : null;
}

var settings = new WebSettings
{
    ListenUrl = Environment.GetEnvironmentVariable("ORKIS_WEB_LISTEN") ?? "http://127.0.0.1:7420",
    DaemonSocketPath = daemonSocket,
    BearerToken = token,
    RequireAuthOnLoopback = Environment.GetEnvironmentVariable("ORKIS_WEB_REQUIRE_AUTH") == "1",
    AssetsPath = assetsPath,
};

var app = GatewayApplication.Create(settings);

Console.WriteLine($"orkis web | listening on {settings.ListenUrl}");
Console.WriteLine($"daemon: unix:{settings.DaemonSocketPath}");
Console.WriteLine($"assets: {settings.AssetsPath ?? "(not built — placeholder page)"}");
Console.WriteLine(
    settings.RequireAuthOnLoopback
        ? "auth: token required on every request"
        : "auth: loopback exempt; remote requests need the token"
);

await app.RunAsync();
