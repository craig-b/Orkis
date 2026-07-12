using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Orkis.Retrieval;

namespace Orkis.Rag.Tests;

public sealed class ChatClientRerankerTests : IDisposable
{
    private readonly ScriptedChatClient _chatClient = new();

    public void Dispose() => _chatClient.Dispose();

    private ChatClientReranker CreateReranker(int? maxPassageCharacters = null) =>
        new(
            _chatClient,
            maxPassageCharacters is { } max
                ? Options.Create(new ChatClientRerankerOptions { MaxPassageCharacters = max })
                : null
        );

    private static Scored<Chunk> Candidate(string id, string text, double score = 0.5) =>
        new(new Chunk { Id = id, Text = text }, score);

    [Fact]
    public async Task ReordersCandidatesByModelScores()
    {
        _chatClient.Enqueue("""[{"index":1,"score":3},{"index":2,"score":9},{"index":3,"score":7}]""");
        var reranker = CreateReranker();

        var result = await reranker.RerankAsync(
            "how do cats purr",
            [Candidate("a", "Dogs bark."), Candidate("b", "Cats purr by vibrating."), Candidate("c", "Cats sleep.")]
        );

        Assert.Equal(["b", "c", "a"], result.Select(r => r.Item.Id));
        Assert.Equal([9d, 7d, 3d], result.Select(r => r.Score));
    }

    [Fact]
    public async Task PromptContainsQueryAndNumberedPassages()
    {
        _chatClient.Enqueue("""[{"index":1,"score":5},{"index":2,"score":5}]""");

        await CreateReranker()
            .RerankAsync("the query text", [Candidate("a", "first passage"), Candidate("b", "second passage")]);

        var prompt = Assert.Single(_chatClient.Requests).Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("the query text", prompt, StringComparison.Ordinal);
        Assert.Contains("[1]", prompt, StringComparison.Ordinal);
        Assert.Contains("first passage", prompt, StringComparison.Ordinal);
        Assert.Contains("[2]", prompt, StringComparison.Ordinal);
        Assert.Contains("second passage", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnscoredCandidatesSortLastWithZeroScore()
    {
        _chatClient.Enqueue("""[{"index":2,"score":8}]""");

        var result = await CreateReranker().RerankAsync("q", [Candidate("a", "one"), Candidate("b", "two")]);

        Assert.Equal(["b", "a"], result.Select(r => r.Item.Id));
        Assert.Equal(0d, result[1].Score);
    }

    [Fact]
    public async Task ToleratesProseAndCodeFencesAroundTheArray()
    {
        _chatClient.Enqueue(
            """
            Here are the scores:
            ```json
            [{"index":1,"score":2},{"index":2,"score":6}]
            ```
            """
        );

        var result = await CreateReranker().RerankAsync("q", [Candidate("a", "one"), Candidate("b", "two")]);

        Assert.Equal(["b", "a"], result.Select(r => r.Item.Id));
    }

    [Fact]
    public async Task OutOfRangeAndDuplicateIndicesAreIgnored()
    {
        _chatClient.Enqueue(
            """[{"index":1,"score":4},{"index":1,"score":9},{"index":7,"score":10},{"index":0,"score":10}]"""
        );

        var result = await CreateReranker().RerankAsync("q", [Candidate("a", "one"), Candidate("b", "two")]);

        Assert.Equal(["a", "b"], result.Select(r => r.Item.Id));
        Assert.Equal([4d, 0d], result.Select(r => r.Score));
    }

    [Fact]
    public async Task MalformedResponseThrows()
    {
        _chatClient.Enqueue("I cannot score these passages.");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateReranker().RerankAsync("q", [Candidate("a", "one")])
        );
    }

    [Fact]
    public async Task EmptyCandidateListReturnsEmptyWithoutCallingTheModel()
    {
        var result = await CreateReranker().RerankAsync("q", []);

        Assert.Empty(result);
        Assert.Empty(_chatClient.Requests);
    }

    [Fact]
    public async Task LongPassagesAreTruncatedInThePrompt()
    {
        _chatClient.Enqueue("""[{"index":1,"score":5}]""");
        var longText = new string('x', 100);

        await CreateReranker(maxPassageCharacters: 10).RerankAsync("q", [Candidate("a", longText)]);

        var prompt = Assert.Single(_chatClient.Requests).Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains(new string('x', 10) + "…", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 11), prompt, StringComparison.Ordinal);
    }
}

/// <summary>An <see cref="IChatClient"/> that replays scripted text responses in order.</summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<string> _responses = new();

    public List<List<ChatMessage>> Requests { get; } = [];

    public void Enqueue(string text) => _responses.Enqueue(text);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        Requests.Add([.. messages]);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _responses.Dequeue())));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
