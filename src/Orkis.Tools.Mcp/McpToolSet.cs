using ModelContextProtocol.Client;

namespace Orkis.Tools;

/// <summary>
/// A live connection to one MCP server and the Orkis tools it contributes. Register
/// the tools always-on or hand them to the tool catalogue — the natural home for
/// large servers, where the model discovers them via <c>search_tools</c> instead of
/// every schema occupying the context window. Dispose to shut the server down.
/// </summary>
public sealed class McpToolSet : IAsyncDisposable
{
    private readonly McpClient _client;

    private McpToolSet(McpClient client, IReadOnlyList<ITool> tools)
    {
        _client = client;
        Tools = tools;
    }

    /// <summary>The server's tools, adapted to <see cref="ITool"/>.</summary>
    public IReadOnlyList<ITool> Tools { get; }

    /// <summary>
    /// Connects from a single configuration string — how hosts read
    /// <c>ORKIS_MCP_SERVER</c>: an <c>http(s)://</c> value is a Streamable HTTP
    /// endpoint, anything else a command line launched over stdio. The server's
    /// display name defaults to the endpoint host or the command's file name.
    /// </summary>
    public static Task<McpToolSet> ConnectAsync(string serverSpec, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverSpec);

        if (
            serverSpec.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || serverSpec.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
        {
            var endpoint = new Uri(serverSpec);
            return ConnectAsync(
                new McpHttpServerOptions { Endpoint = endpoint, Name = endpoint.Host },
                cancellationToken
            );
        }

        var parts = serverSpec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var options = new McpStdioServerOptions { Command = parts[0], Name = Path.GetFileName(parts[0]) };
        foreach (var argument in parts.Skip(1))
        {
            options.Arguments.Add(argument);
        }

        return ConnectAsync(options, cancellationToken);
    }

    /// <summary>Launches the server over stdio and lists its tools.</summary>
    public static Task<McpToolSet> ConnectAsync(
        McpStdioServerOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = options.Command,
                Arguments = [.. options.Arguments],
                EnvironmentVariables = new Dictionary<string, string?>(options.EnvironmentVariables),
                Name = options.Name,
            }
        );
        return ConnectAsync(transport, options, cancellationToken);
    }

    /// <summary>Connects to a remote server over Streamable HTTP and lists its tools.</summary>
    public static Task<McpToolSet> ConnectAsync(
        McpHttpServerOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = options.Endpoint,
                AdditionalHeaders = new Dictionary<string, string>(options.Headers),
                Name = options.Name,
            }
        );
        return ConnectAsync(transport, options, cancellationToken);
    }

    private static async Task<McpToolSet> ConnectAsync(
        IClientTransport transport,
        McpServerOptionsBase options,
        CancellationToken cancellationToken
    )
    {
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new McpToolSet(
                client,
                [.. tools.Select(tool => new McpToolAdapter(client, tool, options.TrustAnnotations))]
            );
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
