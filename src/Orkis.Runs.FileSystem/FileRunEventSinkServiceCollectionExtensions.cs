using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Runs;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the file-system run-event log.</summary>
public static class FileRunEventSinkServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="FileRunEventSink"/> as both <see cref="IRunEventSink"/> and
    /// <see cref="IRunEventLog"/>, so every run leaves a durable JSON-lines event
    /// history. Replaces any existing registrations; call order does not matter.
    /// </summary>
    public static IServiceCollection AddOrkisFileRunEvents(
        this IServiceCollection services,
        Action<FileRunEventSinkOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services
            .AddOptions<FileRunEventSinkOptions>()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.RootPath),
                $"{nameof(FileRunEventSinkOptions)}.{nameof(FileRunEventSinkOptions.RootPath)} must be set."
            )
            .ValidateOnStart();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.TryAddSingleton<FileRunEventSink>();
        services.RemoveAll<IRunEventSink>();
        services.RemoveAll<IRunEventLog>();
        services.AddSingleton<IRunEventSink>(static sp => sp.GetRequiredService<FileRunEventSink>());
        services.AddSingleton<IRunEventLog>(static sp => sp.GetRequiredService<FileRunEventSink>());
        return services;
    }
}
