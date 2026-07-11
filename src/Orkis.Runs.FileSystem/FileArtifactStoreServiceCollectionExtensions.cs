using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Artifacts;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the file-system artifact store.</summary>
public static class FileArtifactStoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="FileArtifactStore"/> as the <see cref="IArtifactStore"/>
    /// implementation. Replaces any existing registration, so call order relative to
    /// other registrations does not matter.
    /// </summary>
    public static IServiceCollection AddOrkisFileArtifactStore(
        this IServiceCollection services,
        Action<FileArtifactStoreOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services
            .AddOptions<FileArtifactStoreOptions>()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.RootPath),
                $"{nameof(FileArtifactStoreOptions)}.{nameof(FileArtifactStoreOptions.RootPath)} must be set."
            )
            .ValidateOnStart();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<IArtifactStore>();
        services.AddSingleton<IArtifactStore, FileArtifactStore>();
        return services;
    }
}
