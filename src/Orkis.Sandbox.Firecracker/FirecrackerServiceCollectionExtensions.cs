using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Sandboxing;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Firecracker micro-VM sandbox.</summary>
public static class FirecrackerServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="FirecrackerSandbox"/> as the <see cref="ISandbox"/> implementation,
    /// providing <see cref="SandboxLevel.Strict"/> isolation via KVM micro-VMs. The kernel
    /// and rootfs image paths must be configured; run scripts/setup-firecracker.sh to
    /// provision them, and probe <see cref="FirecrackerSandbox.IsSupported"/> for availability.
    /// </summary>
    public static IServiceCollection AddOrkisFirecrackerSandbox(
        this IServiceCollection services,
        Action<FirecrackerSandboxOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<FirecrackerSandboxOptions>().Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISandbox, FirecrackerSandbox>();
        return services;
    }
}
