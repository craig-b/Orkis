using Microsoft.Extensions.DependencyInjection.Extensions;
using Orkis.Agents;
using Orkis.Runs;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Orkis core services.</summary>
public static class OrkisServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Orkis agent runner and default in-memory checkpointing. The host must
    /// also register an <see cref="Microsoft.Extensions.AI.IChatClient"/>, an
    /// <see cref="Orkis.Supervision.ISupervisor"/>, and any <see cref="Orkis.Tools.ITool"/>
    /// implementations the agent should have.
    /// </summary>
    public static IServiceCollection AddOrkis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICheckpointStore, InMemoryCheckpointStore>();
        services.TryAddSingleton<AgentRunner>();
        return services;
    }
}
