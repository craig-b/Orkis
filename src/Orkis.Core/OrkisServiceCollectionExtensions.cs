using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Agents;
using Orkis.Runs;
using Orkis.Supervision;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Orkis core services.</summary>
public static class OrkisServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Orkis agent runner, keyed supervisor resolution, and default in-memory
    /// checkpointing. The host must also register an
    /// <see cref="Microsoft.Extensions.AI.IChatClient"/>, at least one supervisor via
    /// <see cref="AddOrkisSupervisor{TSupervisor}"/>, and any <see cref="Orkis.Tools.ITool"/>
    /// implementations the agent should have.
    /// </summary>
    public static IServiceCollection AddOrkis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICheckpointStore, InMemoryCheckpointStore>();
        services.TryAddSingleton<ISupervisorResolver, KeyedServiceSupervisorResolver>();
        services.TryAddSingleton<AgentRunner>();
        return services;
    }

    /// <summary>
    /// Registers a supervisor under <paramref name="key"/>. Runs select their supervisor
    /// by key via <see cref="Orkis.Agents.AgentRunRequest.SupervisorKey"/>.
    /// </summary>
    public static IServiceCollection AddOrkisSupervisor<TSupervisor>(
        this IServiceCollection services,
        string key = SupervisorKeys.Default
    )
        where TSupervisor : class, ISupervisor
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(key);

        services.AddKeyedSingleton<ISupervisor, TSupervisor>(key);
        return services;
    }

    /// <summary>
    /// Registers a supervisor built by <paramref name="factory"/> under <paramref name="key"/>,
    /// for composed policies such as <see cref="ThresholdSupervisor"/> wrapping an escalation target.
    /// </summary>
    public static IServiceCollection AddOrkisSupervisor(
        this IServiceCollection services,
        string key,
        Func<IServiceProvider, ISupervisor> factory
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddKeyedSingleton<ISupervisor>(key, (provider, _) => factory(provider));
        return services;
    }
}
