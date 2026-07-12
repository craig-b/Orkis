using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Memory;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the SQLite-backed memory store.</summary>
public static class SqliteMemoryStoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="SqliteMemoryStore"/> as the <see cref="IMemoryStore"/>
    /// implementation. Replaces any existing registration, so call order does not
    /// matter. The host must register an embedding generator.
    /// </summary>
    public static IServiceCollection AddOrkisSqliteMemoryStore(
        this IServiceCollection services,
        Action<SqliteMemoryStoreOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services
            .AddOptions<SqliteMemoryStoreOptions>()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.DatabasePath),
                $"{nameof(SqliteMemoryStoreOptions)}.{nameof(SqliteMemoryStoreOptions.DatabasePath)} must be set."
            )
            .ValidateOnStart();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.RemoveAll<IMemoryStore>();
        services.AddSingleton<IMemoryStore, SqliteMemoryStore>();
        return services;
    }
}
