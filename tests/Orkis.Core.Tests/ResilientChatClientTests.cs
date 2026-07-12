using System.Net;
using Microsoft.Extensions.AI;
using Orkis.Clients;

namespace Orkis.Core.Tests;

public sealed class ResilientChatClientTests
{
    /// <summary>Replays a script of exceptions and responses, counting attempts.</summary>
    private sealed class FlakyChatClient : IChatClient
    {
        private readonly Queue<object> _script = new();

        public int Attempts { get; private set; }

        public FlakyChatClient Throws(Exception exception)
        {
            _script.Enqueue(exception);
            return this;
        }

        public FlakyChatClient Succeeds(string text = "ok")
        {
            _script.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
            return this;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Attempts++;
            return _script.Dequeue() switch
            {
                Exception exception => Task.FromException<ChatResponse>(exception),
                ChatResponse response => Task.FromResult(response),
                _ => throw new InvalidOperationException(),
            };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static readonly ResilientChatClientOptions FastOptions = new()
    {
        MaxAttempts = 4,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(5),
    };

    private static HttpRequestException RateLimited() => new("429", null, HttpStatusCode.TooManyRequests);

    private static Task<ChatResponse> CallAsync(
        FlakyChatClient inner,
        ResilientChatClientOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        using var client = new ResilientChatClient(inner, options ?? FastOptions);
        return client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, cancellationToken);
    }

    [Fact]
    public async Task RetriesTransientFailuresUntilSuccess()
    {
        var inner = new FlakyChatClient().Throws(RateLimited()).Throws(new TimeoutException()).Succeeds("recovered");

        var response = await CallAsync(inner);

        Assert.Equal("recovered", response.Text);
        Assert.Equal(3, inner.Attempts);
    }

    [Fact]
    public async Task GivesUpAfterMaxAttempts()
    {
        var inner = new FlakyChatClient().Throws(RateLimited()).Throws(RateLimited());
        var options = new ResilientChatClientOptions { MaxAttempts = 2, BaseDelay = TimeSpan.FromMilliseconds(1) };

        await Assert.ThrowsAsync<HttpRequestException>(() => CallAsync(inner, options));
        Assert.Equal(2, inner.Attempts);
    }

    [Fact]
    public async Task NonTransientFailuresAreNotRetried()
    {
        var inner = new FlakyChatClient().Throws(new InvalidOperationException("bad request shape"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(inner));
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task ClientErrorsAreNotRetried()
    {
        var inner = new FlakyChatClient().Throws(new HttpRequestException("400", null, HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<HttpRequestException>(() => CallAsync(inner));
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task CallerCancellationIsNotRetried()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var inner = new FlakyChatClient().Throws(new TaskCanceledException());

        await Assert.ThrowsAsync<TaskCanceledException>(() => CallAsync(inner, cancellationToken: cancelled.Token));
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task CustomShouldRetryOverridesTheDefault()
    {
        var inner = new FlakyChatClient().Throws(new InvalidOperationException("provider hiccup")).Succeeds();
        var options = new ResilientChatClientOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(1),
            ShouldRetry = static ex => ex is InvalidOperationException,
        };

        var response = await CallAsync(inner, options);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, inner.Attempts);
    }
}
