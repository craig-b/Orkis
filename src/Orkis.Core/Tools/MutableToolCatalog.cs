using System.Collections.Concurrent;
using Orkis.Tools;

namespace Orkis.Core.Tools;

/// <summary>
/// A tool catalogue whose contents change at runtime: tools arrive and leave in named
/// groups (an MCP server contributes one group), so a server can be connected or
/// disconnected on a live daemon without a restart. Search and resolution see the
/// current set — a run that has activated a tool re-resolves it each turn, so a tool
/// from a removed group simply drops out. Same keyword scoring as
/// <see cref="InMemoryToolCatalog"/>; the difference is only mutability.
/// </summary>
public sealed class MutableToolCatalog : IToolCatalog
{
    // Grouped so removal is atomic; a flattened view is rebuilt on each change, which is
    // cheap for the handful of servers and dozens of tools a daemon realistically holds.
    private readonly ConcurrentDictionary<string, IReadOnlyList<ITool>> _groups = new(StringComparer.Ordinal);
    private volatile IReadOnlyDictionary<string, ITool> _tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
    private readonly Lock _rebuild = new();

    public MutableToolCatalog(IEnumerable<KeyValuePair<string, IReadOnlyList<ITool>>>? groups = null)
    {
        if (groups is not null)
        {
            foreach (var (name, tools) in groups)
            {
                _groups[name] = tools;
            }

            Rebuild();
        }
    }

    /// <inheritdoc />
    public int Count => _tools.Count;

    /// <summary>Adds or replaces a named group of tools, then refreshes the flattened view.</summary>
    public void SetGroup(string name, IReadOnlyList<ITool> tools)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(tools);
        _groups[name] = tools;
        Rebuild();
    }

    /// <summary>Removes a named group; returns <see langword="false"/> when it was not present.</summary>
    public bool RemoveGroup(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_groups.TryRemove(name, out _))
        {
            return false;
        }

        Rebuild();
        return true;
    }

    private void Rebuild()
    {
        lock (_rebuild)
        {
            // Last writer wins on a name collision across groups — deterministic, and
            // MCP tool names are server-unique in practice.
            var flattened = new Dictionary<string, ITool>(StringComparer.Ordinal);
            foreach (var tools in _groups.Values)
            {
                foreach (var tool in tools)
                {
                    flattened[tool.Descriptor.Name] = tool;
                }
            }

            _tools = flattened;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ToolDescriptor>> SearchAsync(
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var terms = query.Split(
            [' ', '\t', '\n', ',', '.', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        var matches = _tools
            .Values.Select(tool => (tool.Descriptor, Score: Score(tool.Descriptor, terms)))
            .Where(static match => match.Score > 0)
            .OrderByDescending(static match => match.Score)
            .Take(limit)
            .Select(static match => match.Descriptor)
            .ToList();

        return Task.FromResult<IReadOnlyList<ToolDescriptor>>(matches);
    }

    /// <inheritdoc />
    public Task<ITool?> ResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Task.FromResult(_tools.TryGetValue(name, out var tool) ? tool : null);
    }

    private static int Score(ToolDescriptor descriptor, string[] terms) =>
        terms.Count(term =>
            descriptor.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || descriptor.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        );
}
