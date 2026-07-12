using Microsoft.Extensions.AI;

namespace Orkis.Memory.Sqlite.Tests;

/// <summary>
/// Deterministic embeddings from hashed words: texts sharing words get similar
/// vectors, so cosine ranking behaves like a (crude) real embedding in tests.
/// </summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = 64;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var embeddings = values.Select(static v => new Embedding<float>(Embed(v))).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    private static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (
            var word in text.ToLowerInvariant()
                .Split([' ', '\n', '.', ',', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
        )
        {
            vector[StableHash(word) % Dimensions]++;
        }

        return vector;
    }

    private static uint StableHash(string word)
    {
        var hash = 2166136261u;
        foreach (var c in word)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
