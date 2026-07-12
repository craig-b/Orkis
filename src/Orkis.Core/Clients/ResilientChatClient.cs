using System.Net;
using Microsoft.Extensions.AI;
using Orkis.Diagnostics;

namespace Orkis.Clients;

/// <summary>
/// Retries transient model-call failures (network errors, timeouts, HTTP 408/429/5xx)
/// with exponential backoff and full jitter. Composes at the client-builder level, so
/// every consumer of the client — the agent loop, the AI supervisor, the reranker —
/// gets the same behavior without any of them knowing. Only whole-call retries:
/// streaming passes through unretried, and tool-call errors stay the model's problem
/// (it sees typed error results and decides). Failed attempts return no usage, so
/// budgets naturally count only the response that actually arrived.
/// </summary>
public sealed class ResilientChatClient : DelegatingChatClient
{
    private readonly ResilientChatClientOptions _options;
    private readonly TimeProvider _timeProvider;

    public ResilientChatClient(
        IChatClient innerClient,
        ResilientChatClientOptions? options = null,
        TimeProvider? timeProvider = null
    )
        : base(innerClient)
    {
        _options = options ?? new ResilientChatClientOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (attempt < _options.MaxAttempts && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                OrkisInstrumentation.ModelRetries.Add(1);

                // Full jitter: a uniformly random slice of the exponential window, so
                // concurrent callers hitting the same rate limit spread back out.
                var window = Math.Min(
                    _options.MaxDelay.TotalMilliseconds,
                    _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)
                );
                var delay = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * window);
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool IsTransient(Exception exception)
    {
        if (_options.ShouldRetry is { } shouldRetry)
        {
            return shouldRetry(exception);
        }

        return exception switch
        {
            HttpRequestException http => http.StatusCode
                is null
                    or HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    or >= HttpStatusCode.InternalServerError,
            TimeoutException => true,
            // An HttpClient timeout surfaces as cancellation the caller never asked for.
            TaskCanceledException => true,
            _ => false,
        };
    }
}
