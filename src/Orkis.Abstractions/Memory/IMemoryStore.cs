namespace Orkis.Memory;

/// <summary>Durable storage for agent-written memories, searchable by relevance.</summary>
public interface IMemoryStore
{
    /// <summary>Stores a memory, replacing any existing memory with the same id.</summary>
    Task WriteAsync(MemoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Returns the memories most relevant to <paramref name="query"/>, best first.</summary>
    Task<IReadOnlyList<Scored<MemoryEntry>>> SearchAsync(
        string query,
        int topK = 8,
        CancellationToken cancellationToken = default
    );

    /// <summary>Deletes the memory with the given id, if it exists.</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
