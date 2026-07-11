namespace Orkis.Retrieval;

/// <summary>Retrieves chunks relevant to a query from an indexed corpus.</summary>
public interface IRetriever
{
    /// <summary>Returns the chunks most relevant to <paramref name="query"/>, best first.</summary>
    Task<IReadOnlyList<Scored<Chunk>>> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken cancellationToken = default
    );
}
