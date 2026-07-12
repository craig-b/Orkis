using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orkis.Daemon;

/// <summary>
/// The daemon's config file (JSONC — comments and trailing commas allowed): named
/// providers (endpoint + credentials + kind) and models (a provider plus its model
/// id), the default model, and the embedding model. Loaded once at startup; this is
/// boot-only config, distinct from runtime objects mutated over the API.
/// </summary>
public sealed class OrkisConfig
{
    public Dictionary<string, ProviderConfig> Providers { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, ModelConfig> Models { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Key of the model a run uses when it names none.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>The model used for embeddings (memory and corpus retrieval); its provider must be OpenAI-kind.</summary>
    public EmbeddingConfig? Embedding { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// The config file path: <c>ORKIS_CONFIG</c>, else <c>$XDG_CONFIG_HOME/orkis/config.json</c>,
    /// else <c>~/.config/orkis/config.json</c>.
    /// </summary>
    public static string DefaultPath()
    {
        if (Environment.GetEnvironmentVariable("ORKIS_CONFIG") is { Length: > 0 } explicitPath)
        {
            return explicitPath;
        }

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "orkis", "config.json");
    }

    /// <summary>Reads and resolves the config file, or <see langword="null"/> when none exists.</summary>
    public static ResolvedConfig? Load(string? path = null)
    {
        path ??= DefaultPath();
        if (!File.Exists(path))
        {
            return null;
        }

        OrkisConfig config;
        try
        {
            config =
                JsonSerializer.Deserialize<OrkisConfig>(File.ReadAllText(path), JsonOptions)
                ?? throw new OrkisConfigException($"Config file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new OrkisConfigException($"Config file '{path}' is not valid JSON: {ex.Message}", ex);
        }

        return config.Resolve(path);
    }

    private ResolvedConfig Resolve(string path)
    {
        ResolvedModel ResolveModel(string key, string providerName, string modelId)
        {
            if (!Providers.TryGetValue(providerName, out var provider))
            {
                throw new OrkisConfigException(
                    $"Model '{key}' references unknown provider '{providerName}' in {path}."
                );
            }

            var kind = provider.Kind?.ToLowerInvariant() switch
            {
                "anthropic" => ProviderKind.Anthropic,
                "openai" => ProviderKind.OpenAI,
                var other => throw new OrkisConfigException(
                    $"Provider '{providerName}' has unknown kind '{other}' (expected anthropic or openai) in {path}."
                ),
            };

            var apiKey = ResolveSecret(provider, providerName, path);
            return new ResolvedModel(key, kind, provider.BaseUrl, apiKey, modelId);
        }

        var models = Models.Select(entry => ResolveModel(entry.Key, entry.Value.Provider, entry.Value.Model)).ToList();

        if (models.Count == 0)
        {
            throw new OrkisConfigException($"Config file '{path}' declares no models.");
        }

        var defaultKey = DefaultModel ?? models[0].Key;
        if (models.All(model => model.Key != defaultKey))
        {
            throw new OrkisConfigException($"defaultModel '{defaultKey}' is not one of the declared models in {path}.");
        }

        ResolvedModel? embedding = null;
        if (Embedding is { } embeddingConfig)
        {
            embedding = ResolveModel(
                $"embedding:{embeddingConfig.Model}",
                embeddingConfig.Provider,
                embeddingConfig.Model
            );
            if (embedding.Kind != ProviderKind.OpenAI)
            {
                throw new OrkisConfigException(
                    $"The embedding provider '{embeddingConfig.Provider}' must be OpenAI-kind (Anthropic has no embeddings) in {path}."
                );
            }
        }

        return new ResolvedConfig(models, defaultKey, embedding);
    }

    private static string ResolveSecret(ProviderConfig provider, string providerName, string path)
    {
        if (provider.ApiKey is { Length: > 0 } inline)
        {
            return inline;
        }

        if (provider.ApiKeyEnv is { Length: > 0 } envName)
        {
            return Environment.GetEnvironmentVariable(envName) is { Length: > 0 } value
                ? value
                : throw new OrkisConfigException(
                    $"Provider '{providerName}' references environment variable '{envName}', which is not set ({path})."
                );
        }

        throw new OrkisConfigException($"Provider '{providerName}' has neither apiKey nor apiKeyEnv in {path}.");
    }
}

/// <summary>A named provider: an endpoint and credentials speaking a given API kind.</summary>
public sealed class ProviderConfig
{
    /// <summary><c>anthropic</c> or <c>openai</c> (OpenAI-compatible endpoints use <c>openai</c> + baseUrl).</summary>
    public string? Kind { get; init; }

    /// <summary>Base URL for an OpenAI-compatible endpoint (OpenRouter, a local server, …); omit for the default.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>The API key inline. Prefer <see cref="ApiKeyEnv"/> to keep secrets out of the file.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Name of an environment variable holding the API key.</summary>
    public string? ApiKeyEnv { get; init; }
}

/// <summary>A named model: a provider and the model id that provider knows it by.</summary>
public sealed class ModelConfig
{
    public required string Provider { get; init; }

    public required string Model { get; init; }
}

/// <summary>The embedding model selection.</summary>
public sealed class EmbeddingConfig
{
    public required string Provider { get; init; }

    public required string Model { get; init; }
}

/// <summary>The resolved, validated config the daemon builds clients from.</summary>
public sealed record ResolvedConfig(
    IReadOnlyList<ResolvedModel> Models,
    string DefaultModelKey,
    ResolvedModel? Embedding
);

/// <summary>A config file that could not be read or validated.</summary>
public sealed class OrkisConfigException : Exception
{
    public OrkisConfigException(string message, Exception? inner = null)
        : base(message, inner) { }
}
