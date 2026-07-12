using System.Collections.Frozen;
using Orkis.Tools;

namespace Orkis.Core.Tools;

/// <summary>
/// A fixed, in-process tool catalogue. Search is case-insensitive term matching over
/// each tool's name and description, scored by how many query terms match — simple,
/// deterministic, and good enough until a catalogue outgrows keyword lookup.
/// </summary>
public sealed class InMemoryToolCatalog : IToolCatalog
{
    private readonly FrozenDictionary<string, ITool> _tools;

    public InMemoryToolCatalog(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToFrozenDictionary(static tool => tool.Descriptor.Name);
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
