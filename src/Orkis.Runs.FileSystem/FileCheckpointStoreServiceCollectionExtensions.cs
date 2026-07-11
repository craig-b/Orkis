using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Runs;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the file-system checkpoint store.</summary>
public static class FileCheckpointStoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="FileCheckpointStore"/> as the <see cref="ICheckpointStore"/>
    /// implementation so checkpoints survive a process restart. Replaces any existing
    /// registration (such as the in-memory default from <c>AddOrkis</c>), so call
    /// order relative to <c>AddOrkis</c> does not matter.
    /// </summary>
    public static IServiceCollection AddOrkisFileCheckpointStore(
        this IServiceCollection services,
        Action<FileCheckpointStoreOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services
            .AddOptions<FileCheckpointStoreOptions>()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.RootPath),
                $"{nameof(FileCheckpointStoreOptions)}.{nameof(FileCheckpointStoreOptions.RootPath)} must be set."
            )
            .ValidateOnStart();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.RemoveAll<ICheckpointStore>();
        services.AddSingleton<ICheckpointStore, FileCheckpointStore>();
        return services;
    }
}
