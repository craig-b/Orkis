using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Orkis.Clients;

/// <summary>
/// Resolves a chat client from a run's model key. Mirrors supervisor resolution: the
/// key is part of the run's checkpointed state, so a resumed run reconnects to the
/// same model even in a different process.
/// </summary>
public interface IChatClientResolver
{
    /// <summary>
    /// Returns the chat client registered under <paramref name="modelKey"/>. Throws
    /// <see cref="InvalidOperationException"/> with an actionable message when no such
    /// model is registered — a removed model is a typed outcome, not a crash.
    /// </summary>
    IChatClient Resolve(string modelKey);
}

/// <summary>Resolves chat clients registered as keyed services in the host's container.</summary>
public sealed class KeyedServiceChatClientResolver(IServiceProvider serviceProvider) : IChatClientResolver
{
    /// <inheritdoc />
    public IChatClient Resolve(string modelKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelKey);

        return serviceProvider.GetKeyedService<IChatClient>(modelKey)
            ?? throw new InvalidOperationException(
                $"No chat client is registered under model key '{modelKey}'; register one with AddOrkisChatClient."
            );
    }
}
