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

    /// <summary>
    /// Adds <see cref="HostSandbox"/> as an <see cref="ISandbox"/> providing
    /// <see cref="SandboxLevel.None"/> — direct, unshielded execution on the host.
    /// Registered additively so it can sit alongside an isolation sandbox; a caller
    /// resolving <see cref="IEnumerable{T}"/> of <see cref="ISandbox"/> can then choose
    /// per call. This is not containment — see <see cref="HostSandbox"/>.
    /// </summary>
    public static IServiceCollection AddOrkisHostSandbox(
        this IServiceCollection services,
        Action<HostSandboxOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<HostSandboxOptions>();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISandbox, HostSandbox>();
        return services;
    }
}
