using Microsoft.Extensions.DependencyInjection;

namespace Orkis.Supervision;

/// <summary>Resolves supervisors registered as keyed services in the host's container.</summary>
public sealed class KeyedServiceSupervisorResolver(IServiceProvider serviceProvider) : ISupervisorResolver
{
    /// <inheritdoc />
    public ISupervisor Resolve(string key) => serviceProvider.GetRequiredKeyedService<ISupervisor>(key);
}
