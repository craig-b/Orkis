using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Retrieval;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Orkis RAG building blocks.</summary>
public static class RagServiceCollectionExtensions
{
    /// <summary>
    /// Adds the ingestion pipeline (plain-text parsing, paragraph-aware chunking) and the
    /// in-memory vector store as both <see cref="IChunkStore"/> and <see cref="IRetriever"/>.
    /// The host must register an
    /// <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput, TEmbedding}"/> of
    /// <see cref="string"/> to <see cref="Microsoft.Extensions.AI.Embedding{T}"/> of <see cref="float"/>.
    /// </summary>
    public static IServiceCollection AddOrkisInMemoryRag(
        this IServiceCollection services,
        Action<TextChunkerOptions>? configureChunker = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<TextChunkerOptions>();
        if (configureChunker is not null)
        {
            options.Configure(configureChunker);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentParser, PlainTextParser>());
        services.TryAddSingleton<IChunker, TextChunker>();
        services.TryAddSingleton<InMemoryVectorStore>();
        services.TryAddSingleton<IChunkStore>(static sp => sp.GetRequiredService<InMemoryVectorStore>());
        services.TryAddSingleton<IRetriever>(static sp => sp.GetRequiredService<InMemoryVectorStore>());
        services.TryAddSingleton<DocumentIngestor>();
        return services;
    }
}
