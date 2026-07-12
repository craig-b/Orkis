using System.Collections.Concurrent;
using Orkis.Client;
using Orkis.Core.Tools;
using Orkis.Tools;

namespace Orkis.Daemon;

/// <summary>
/// The live set of MCP servers the daemon has connected. Boot-time servers seed it, and
/// the <c>/v1/mcp-servers</c> endpoints add and remove more at runtime — each server's
/// tools flow into the shared <see cref="MutableToolCatalog"/>, so a run's tool search
/// reflects connections and disconnections without a daemon restart. Disposed with the
/// container, which shuts every server down.
/// </summary>
public sealed class McpServerRegistry : IAsyncDisposable
{
    private readonly MutableToolCatalog _catalog;
    private readonly ConcurrentDictionary<string, Entry> _servers = new(StringComparer.Ordinal);

    // Connects run outside this lock; it guards only the name-uniquify-then-insert step
    // so two concurrent adds cannot settle on the same registered name.
    private readonly Lock _register = new();

    private sealed record Entry(string Spec, McpToolSet ToolSet);

    /// <summary>Seeds the registry (and the catalogue) with the boot-time servers.</summary>
    public McpServerRegistry(MutableToolCatalog catalog, IEnumerable<(string Spec, McpToolSet ToolSet)> initial)
    {
        _catalog = catalog;
        foreach (var (spec, toolSet) in initial)
        {
            Register(spec, toolSet);
        }
    }

    /// <summary>Connects a server and registers it, returning how it was recorded.</summary>
    public async Task<McpServerResponse> AddAsync(
        string spec,
        string? name = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        var toolSet = await McpToolSet.ConnectAsync(spec, cancellationToken).ConfigureAwait(false);
        try
        {
            return Describe(Register(spec, toolSet, name));
        }
        catch
        {
            await toolSet.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Disconnects a server; returns <see langword="false"/> when no such name is registered.</summary>
    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_servers.TryRemove(name, out var entry))
        {
            return false;
        }

        _catalog.RemoveGroup(name);
        await entry.ToolSet.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Every connected server, name-ordered.</summary>
    public IReadOnlyList<McpServerResponse> List() =>
        _servers.Keys.OrderBy(name => name, StringComparer.Ordinal).Select(name => Describe(name)).ToList();

    private string Register(string spec, McpToolSet toolSet, string? preferredName = null)
    {
        lock (_register)
        {
            var name = UniqueName(preferredName is { Length: > 0 } given ? given : toolSet.Name);
            _servers[name] = new Entry(spec, toolSet);
            _catalog.SetGroup(name, toolSet.Tools);
            return name;
        }
    }

    // Assumes the register lock is held.
    private string UniqueName(string preferred)
    {
        if (!_servers.ContainsKey(preferred))
        {
            return preferred;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{preferred}-{suffix}";
            if (!_servers.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    private McpServerResponse Describe(string name)
    {
        var entry = _servers[name];
        return new McpServerResponse
        {
            Name = name,
            Server = entry.Spec,
            Tools = entry.ToolSet.Tools.Select(tool => tool.Descriptor.Name).ToList(),
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _servers.Values)
        {
            await entry.ToolSet.DisposeAsync().ConfigureAwait(false);
        }

        _servers.Clear();
    }
}
