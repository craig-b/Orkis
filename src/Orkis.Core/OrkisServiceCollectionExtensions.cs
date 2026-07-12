using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Agents;
using Orkis.Core.Tools;
using Orkis.Runs;
using Orkis.Supervision;
using Orkis.Tools;

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
        services.TryAddSingleton<IApprovalInbox, InMemoryApprovalInbox>();
        services.TryAddSingleton<ISupervisorResolver, KeyedServiceSupervisorResolver>();
        services.TryAddSingleton<ICostCalculator>(NullCostCalculator.Instance);
        services.TryAddSingleton<AgentRunner>();
        return services;
    }

    /// <summary>
    /// Registers an in-memory tool catalogue for progressive disclosure: the supplied
    /// tools are not declared to the model up front — the runner exposes a
    /// <c>search_tools</c> meta-tool instead, and matches become active per run,
    /// recorded in run state. Catalogue tools are deliberately separate from the
    /// always-on <see cref="ITool"/> registrations.
    /// </summary>
    public static IServiceCollection AddOrkisToolCatalog(
        this IServiceCollection services,
        Func<IServiceProvider, IEnumerable<ITool>> tools
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tools);

        services.TryAddSingleton<IToolCatalog>(provider => new InMemoryToolCatalog(tools(provider)));
        return services;
    }

    /// <summary>
    /// Registers <see cref="PriceTableCostCalculator"/> with the given price table,
    /// replacing the default zero-cost calculator. This is what makes
    /// <see cref="Orkis.Runs.RunBudget.MaxCost"/> enforceable.
    /// </summary>
    public static IServiceCollection AddOrkisPricing(this IServiceCollection services, Action<CostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<CostOptions>().Configure(configure);
        services.RemoveAll<ICostCalculator>();
        services.AddSingleton<ICostCalculator, PriceTableCostCalculator>();
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
