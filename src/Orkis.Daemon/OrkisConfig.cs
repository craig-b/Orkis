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

    /// <summary>Root directory for all daemon state (checkpoints, events, …); overridable per-directory by env.</summary>
    public string? DataDir { get; init; }

    /// <summary>The Unix socket path the daemon listens on.</summary>
    public string? Socket { get; init; }

    /// <summary>Isolation sandbox: <c>firecracker</c>, <c>bubblewrap</c>, <c>process</c>, or omit for auto.</summary>
    public string? Sandbox { get; init; }

    /// <summary>Default persistent workspace key.</summary>
    public string? Workspace { get; init; }

    /// <summary>A single MCP server to consume (stdio command line or http(s) endpoint).</summary>
    public string? McpServer { get; init; }

    /// <summary>MCP servers to consume; combined with <see cref="McpServer"/> if both are given.</summary>
    public IReadOnlyList<string>? McpServers { get; init; }

    /// <summary>Directory of documents to index for search_corpus.</summary>
    public string? Corpus { get; init; }

    /// <summary>The MCP servers from both <see cref="McpServer"/> and <see cref="McpServers"/>, de-duplicated.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AllMcpServers =>
        (McpServer is { Length: > 0 } single ? [single, .. McpServers ?? []] : McpServers ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The file this config was read from, for error messages.</summary>
    [JsonIgnore]
    public string SourcePath { get; private set; } = "";

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

    /// <summary>
    /// Reads and deserializes the config file (comments and trailing commas allowed),
    /// or <see langword="null"/> when none exists. Model/provider references are not
    /// resolved yet — call <see cref="ResolveModels"/> for that, so an offline daemon
    /// can read the passthrough settings without requiring provider secrets.
    /// </summary>
    public static OrkisConfig? Load(string? path = null)
    {
        path ??= DefaultPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var config =
                JsonSerializer.Deserialize<OrkisConfig>(File.ReadAllText(path), JsonOptions)
                ?? throw new OrkisConfigException($"Config file '{path}' is empty.");
            config.SourcePath = path;
            return config;
        }
        catch (JsonException ex)
        {
            throw new OrkisConfigException($"Config file '{path}' is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Resolves the declared providers and models into clients-ready records: looks up
    /// each model's provider, resolves its secret, and validates the default and
    /// embedding selections. Throws <see cref="OrkisConfigException"/> on any problem.
    /// </summary>
    public ResolvedConfig ResolveModels()
    {
        var path = SourcePath;

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
