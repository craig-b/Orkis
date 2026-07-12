namespace Orkis.Daemon;

/// <summary>The API shape a provider speaks.</summary>
public enum ProviderKind
{
    /// <summary>The Anthropic Messages API.</summary>
    Anthropic,

    /// <summary>The OpenAI API — or any OpenAI-compatible endpoint via <see cref="ResolvedModel.BaseUrl"/>.</summary>
    OpenAI,
}

/// <summary>
/// A fully-resolved model the daemon can construct a client for: its provider's kind
/// and endpoint, the credentials, and the provider's own model id. Produced from the
/// config file or the legacy environment variables, so the composition has one path.
/// </summary>
public sealed record ResolvedModel(string Key, ProviderKind Kind, string? BaseUrl, string ApiKey, string ModelId);
