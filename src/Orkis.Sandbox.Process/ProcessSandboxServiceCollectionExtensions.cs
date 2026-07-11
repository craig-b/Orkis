using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Sandboxing;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the process-based sandbox.</summary>
public static class ProcessSandboxServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="ProcessSandbox"/> as the <see cref="ISandbox"/> implementation,
    /// providing <see cref="SandboxLevel.Standard"/> isolation via child processes.
    /// </summary>
    public static IServiceCollection AddOrkisProcessSandbox(
        this IServiceCollection services,
        Action<ProcessSandboxOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<ProcessSandboxOptions>();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISandbox, ProcessSandbox>();
        return services;
    }

    /// <summary>
    /// Adds <see cref="BubblewrapSandbox"/> as the <see cref="ISandbox"/> implementation,
    /// providing <see cref="SandboxLevel.Strict"/> isolation via Linux user namespaces.
    /// Requires the bubblewrap binary; probe availability with
    /// <see cref="BubblewrapSandbox.IsSupportedAsync"/>.
    /// </summary>
    public static IServiceCollection AddOrkisBubblewrapSandbox(
        this IServiceCollection services,
        Action<BubblewrapSandboxOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<BubblewrapSandboxOptions>();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISandbox, BubblewrapSandbox>();
        return services;
    }
}
