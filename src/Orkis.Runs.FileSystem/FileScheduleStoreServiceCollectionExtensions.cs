using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Runs;
using Orkis.Scheduling;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the file-system schedule store.</summary>
public static class FileScheduleStoreServiceCollectionExtensions
{
    /// <summary>Adds <see cref="FileScheduleStore"/> as the <see cref="IScheduleStore"/>.</summary>
    public static IServiceCollection AddOrkisFileScheduleStore(
        this IServiceCollection services,
        Action<FileScheduleStoreOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services
            .AddOptions<FileScheduleStoreOptions>()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.RootPath),
                $"{nameof(FileScheduleStoreOptions)}.{nameof(FileScheduleStoreOptions.RootPath)} must be set."
            )
            .ValidateOnStart();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.TryAddSingleton<IScheduleStore, FileScheduleStore>();
        return services;
    }
}
