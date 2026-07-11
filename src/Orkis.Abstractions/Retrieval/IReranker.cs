namespace Orkis.Retrieval;

/// <summary>
/// Second-stage relevance scorer: re-orders retrieval candidates using a more
/// expensive comparison against the query than first-stage retrieval can afford.
/// </summary>
public interface IReranker
{
    /// <summary>Returns the candidates re-scored against <paramref name="query"/>, best first.</summary>
    Task<IReadOnlyList<Scored<Chunk>>> RerankAsync(
        string query,
        IReadOnlyList<Scored<Chunk>> candidates,
        CancellationToken cancellationToken = default
    );
}
