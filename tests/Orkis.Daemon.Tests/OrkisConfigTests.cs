using Orkis.Daemon;

namespace Orkis.Daemon.Tests;

public sealed class OrkisConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("orkis-config-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string jsonc)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, jsonc);
        return path;
    }

    [Fact]
    public void MissingFileReturnsNull()
    {
        Assert.Null(OrkisConfig.Load(Path.Combine(_dir, "nope.json")));
    }

    [Fact]
    public void ResolvesProvidersModelsEmbeddingWithCommentsAndTrailingCommas()
    {
        Environment.SetEnvironmentVariable("TEST_OR_KEY", "sk-or-123");
        try
        {
            var config = OrkisConfig
                .Load(
                    Write(
                        """
                        {
                          // OpenRouter is OpenAI-compatible: kind openai + a base URL.
                          "providers": {
                            "openrouter": { "kind": "openai", "baseUrl": "https://openrouter.ai/api/v1", "apiKeyEnv": "TEST_OR_KEY" },
                            "anthropic":  { "kind": "anthropic", "apiKey": "sk-ant-inline" },
                          },
                          "models": {
                            "sonnet":   { "provider": "anthropic",  "model": "claude-sonnet-5" },
                            "deepseek": { "provider": "openrouter", "model": "deepseek/deepseek-chat" },
                          },
                          "defaultModel": "sonnet",
                          "embedding": { "provider": "openrouter", "model": "text-embedding-3-small" },
                        }
                        """
                    )
                )!
                .ResolveModels();

            Assert.NotNull(config);
            Assert.Equal("sonnet", config.DefaultModelKey);
            Assert.Equal(2, config.Models.Count);

            var deepseek = config.Models.Single(m => m.Key == "deepseek");
            Assert.Equal(ProviderKind.OpenAI, deepseek.Kind);
            Assert.Equal("https://openrouter.ai/api/v1", deepseek.BaseUrl);
            Assert.Equal("sk-or-123", deepseek.ApiKey); // resolved from the env var
            Assert.Equal("deepseek/deepseek-chat", deepseek.ModelId);

            var sonnet = config.Models.Single(m => m.Key == "sonnet");
            Assert.Equal(ProviderKind.Anthropic, sonnet.Kind);
            Assert.Equal("sk-ant-inline", sonnet.ApiKey); // resolved inline

            Assert.NotNull(config.Embedding);
            Assert.Equal(ProviderKind.OpenAI, config.Embedding.Kind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_OR_KEY", null);
        }
    }

    [Fact]
    public void UnknownProviderReferenceIsRejected()
    {
        var ex = Assert.Throws<OrkisConfigException>(() =>
            OrkisConfig
                .Load(
                    Write(
                        """
                        { "providers": {}, "models": { "m": { "provider": "ghost", "model": "x" } } }
                        """
                    )
                )!
                .ResolveModels()
        );
        Assert.Contains("unknown provider", ex.Message);
    }

    [Fact]
    public void MissingEnvSecretIsRejected()
    {
        var ex = Assert.Throws<OrkisConfigException>(() =>
            OrkisConfig
                .Load(
                    Write(
                        """
                        {
                          "providers": { "p": { "kind": "openai", "apiKeyEnv": "DEFINITELY_UNSET_XYZ" } },
                          "models": { "m": { "provider": "p", "model": "gpt" } }
                        }
                        """
                    )
                )!
                .ResolveModels()
        );
        Assert.Contains("DEFINITELY_UNSET_XYZ", ex.Message);
    }

    [Fact]
    public void AnthropicEmbeddingIsRejected()
    {
        var ex = Assert.Throws<OrkisConfigException>(() =>
            OrkisConfig
                .Load(
                    Write(
                        """
                        {
                          "providers": { "a": { "kind": "anthropic", "apiKey": "k" } },
                          "models": { "m": { "provider": "a", "model": "claude" } },
                          "embedding": { "provider": "a", "model": "whatever" }
                        }
                        """
                    )
                )!
                .ResolveModels()
        );
        Assert.Contains("OpenAI-kind", ex.Message);
    }

    [Fact]
    public void DefaultModelMustExist()
    {
        var ex = Assert.Throws<OrkisConfigException>(() =>
            OrkisConfig
                .Load(
                    Write(
                        """
                        {
                          "providers": { "a": { "kind": "anthropic", "apiKey": "k" } },
                          "models": { "m": { "provider": "a", "model": "claude" } },
                          "defaultModel": "nonexistent"
                        }
                        """
                    )
                )!
                .ResolveModels()
        );
        Assert.Contains("defaultModel", ex.Message);
    }

    [Fact]
    public void PassthroughSettingsAreReadWithoutResolvingModels()
    {
        // Offline daemons read dirs/sandbox/etc. from the file without provider secrets.
        var config = OrkisConfig.Load(
            Write(
                """
                {
                  "dataDir": "/srv/orkis",
                  "socket": "/run/orkis/orkis.sock",
                  "sandbox": "bubblewrap",
                  "workspace": "research",
                  "mcpServer": "npx some-mcp-server",
                  "corpus": "/srv/docs",
                  "providers": { "p": { "kind": "openai", "apiKeyEnv": "DEFINITELY_UNSET_XYZ" } },
                  "models": { "m": { "provider": "p", "model": "gpt" } }
                }
                """
            )
        );

        Assert.NotNull(config);
        Assert.Equal("/srv/orkis", config.DataDir);
        Assert.Equal("/run/orkis/orkis.sock", config.Socket);
        Assert.Equal("bubblewrap", config.Sandbox);
        Assert.Equal("research", config.Workspace);
        Assert.Equal("npx some-mcp-server", config.McpServer);
        Assert.Equal("/srv/docs", config.Corpus);
    }

    [Fact]
    public void McpServersCombineSingularAndPluralDeDuplicated()
    {
        var config = OrkisConfig.Load(
            Write(
                """
                {
                  "mcpServer": "npx solo",
                  "mcpServers": ["http://localhost:9000/mcp", "npx solo", "python3 srv.py"],
                  "providers": {},
                  "models": {}
                }
                """
            )
        );

        Assert.NotNull(config);
        // Singular first, then the array, with the duplicate "npx solo" collapsed.
        Assert.Equal(["npx solo", "http://localhost:9000/mcp", "python3 srv.py"], config.AllMcpServers);
    }
}
