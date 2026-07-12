using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Retrieval;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the SQLite-backed vector store.</summary>
public static class SqliteVectorStoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="SqliteVectorStore"/> as both <see cref="IChunkStore"/> and
    /// <see cref="IRetriever"/>, so the indexed corpus survives restarts. Replaces any
    /// existing registrations (such as the in-memory store from
    /// <c>AddOrkisInMemoryRag</c>), so call order does not matter. The host must
    /// register an embedding generator, exactly as for the in-memory store.
    /// </summary>
    public static IServiceCollection AddOrkisSqliteVectorStore(
        this IServiceCollection services,
        Action<SqliteVectorStoreOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services
            .AddOptions<SqliteVectorStoreOptions>()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.DatabasePath),
                $"{nameof(SqliteVectorStoreOptions)}.{nameof(SqliteVectorStoreOptions.DatabasePath)} must be set."
            )
            .ValidateOnStart();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.TryAddSingleton<SqliteVectorStore>();
        services.RemoveAll<IChunkStore>();
        services.RemoveAll<IRetriever>();
        services.AddSingleton<IChunkStore>(static sp => sp.GetRequiredService<SqliteVectorStore>());
        services.AddSingleton<IRetriever>(static sp => sp.GetRequiredService<SqliteVectorStore>());
        return services;
    }
}
