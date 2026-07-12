using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Orkis.Web;

/// <summary>
/// Builds the gateway: the network face the daemon deliberately lacks. Serves the UI
/// assets, exchanges the bearer token for a cookie session (cookies ride
/// <c>EventSource</c>, unlike Authorization headers), and reverse-proxies
/// <c>/v1/*</c> to the daemon's Unix socket with unbuffered streaming, so SSE passes
/// through live.
/// </summary>
public static class GatewayApplication
{
    private const string SessionCookie = "orkis_session";

    /// <summary>Request headers not forwarded to the daemon.</summary>
    private static readonly string[] SkippedRequestHeaders =
    [
        "Host",
        "Connection",
        "Transfer-Encoding",
        "Authorization",
        "Cookie",
        "Content-Length",
        "Content-Type",
    ];

    public static WebApplication Create(WebSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var listen = new Uri(settings.ListenUrl);
        var builder = WebApplication.CreateBuilder();
        if (settings.AssetsPath is { Length: > 0 } assetsPath)
        {
            builder.Environment.WebRootPath = assetsPath;
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(
                listen.Host is "0.0.0.0" or "+" or "*" ? IPAddress.Any : IPAddress.Parse(ResolveHost(listen.Host)),
                listen.Port
            )
        );

        // One connection pool to the daemon for the gateway's lifetime.
        var daemonSocket = settings.DaemonSocketPath;
        builder.Services.AddSingleton(
            new HttpClient(
                new SocketsHttpHandler
                {
                    ConnectCallback = async (_, cancellationToken) =>
                    {
                        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        try
                        {
                            await socket.ConnectAsync(new UnixDomainSocketEndPoint(daemonSocket), cancellationToken);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }

                        return new NetworkStream(socket, ownsSocket: true);
                    },
                }
            )
            {
                BaseAddress = new Uri("http://orkis"),
                Timeout = Timeout.InfiniteTimeSpan, // SSE connections are long-lived.
            }
        );

        var app = builder.Build();
        var sessions = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var expectedToken = Encoding.UTF8.GetBytes(settings.BearerToken);

        // Auth: loopback is trusted like the daemon's socket (unless forced), the
        // login endpoint is reachable by definition, and everything else needs the
        // token or a session.
        app.Use(
            (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/auth", StringComparison.Ordinal))
                {
                    return next(context);
                }

                var loopback = context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote);
                if (loopback && !settings.RequireAuthOnLoopback)
                {
                    return next(context);
                }

                if (IsAuthorized(context, expectedToken, sessions))
                {
                    return next(context);
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return context.Response.WriteAsJsonAsync(new { error = "Sign in with the gateway's token." });
            }
        );

        // Exchange the token for a cookie session — the browser's login.
        app.MapPost(
            "/auth/session",
            (SessionRequest body, HttpContext context) =>
            {
                if (
                    body.Token is not { Length: > 0 }
                    || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(body.Token), expectedToken)
                )
                {
                    return Results.Unauthorized();
                }

                var sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                sessions[sessionId] = DateTimeOffset.UtcNow;
                context.Response.Cookies.Append(
                    SessionCookie,
                    sessionId,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict,
                        Secure = context.Request.IsHttps,
                        MaxAge = TimeSpan.FromDays(7),
                    }
                );
                return Results.NoContent();
            }
        );

        app.Map("/v1/{**path}", ProxyAsync);

        if (settings.AssetsPath is { Length: > 0 })
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }
        else
        {
            app.MapGet(
                "/",
                static () =>
                    Results.Content(
                        "<html><body><h1>Orkis</h1><p>The web UI assets are not built; "
                            + "the <code>/v1</code> API proxy is live.</p></body></html>",
                        "text/html"
                    )
            );
        }

        return app;
    }

    private static bool IsAuthorized(
        HttpContext context,
        byte[] expectedToken,
        ConcurrentDictionary<string, DateTimeOffset> sessions
    )
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (
            authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(authorization["Bearer ".Length..]),
                expectedToken
            )
        )
        {
            return true;
        }

        return context.Request.Cookies.TryGetValue(SessionCookie, out var sessionId)
            && sessionId is not null
            && sessions.ContainsKey(sessionId);
    }

    /// <summary>
    /// Forwards one request to the daemon and streams the response back unbuffered —
    /// a JSON body and an hours-long SSE stream take the same path.
    /// </summary>
    private static async Task ProxyAsync(HttpContext context, HttpClient daemon)
    {
        using var upstream = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            context.Request.Path + context.Request.QueryString
        );
        if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            upstream.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } contentType)
            {
                upstream.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        foreach (var header in context.Request.Headers)
        {
            if (!SkippedRequestHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                upstream.Headers.TryAddWithoutValidation(header.Key, (string?)header.Value);
            }
        }

        using var response = await daemon.SendAsync(
            upstream,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted
        );

        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            // Kestrel owns framing; everything else passes through.
            if (
                !string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(header.Key, "Connection", StringComparison.OrdinalIgnoreCase)
            )
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        await using (body)
        {
            // Flush per chunk so SSE events reach the browser as they happen.
            var buffer = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
    }

    private static string ResolveHost(string host) => host == "localhost" ? "127.0.0.1" : host;

    private sealed record SessionRequest(string? Token);
}
