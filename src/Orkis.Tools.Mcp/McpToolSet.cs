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

    /// <summary>Launches the server over stdio and lists its tools.</summary>
    public static async Task<McpToolSet> ConnectAsync(
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
