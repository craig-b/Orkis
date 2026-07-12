namespace Orkis.Clients;

/// <summary>Configuration for <see cref="ResilientChatClient"/>.</summary>
public sealed class ResilientChatClientOptions
{
    /// <summary>Total attempts per model call, including the first.</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Base delay for exponential backoff; attempt N waits up to base × 2^N.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on any single backoff delay.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Overrides which exceptions count as transient. The default treats network
    /// failures, timeouts, HTTP 408/429, and HTTP 5xx as transient; provider SDKs
    /// that throw their own exception types can be classified here.
    /// </summary>
    public Func<Exception, bool>? ShouldRetry { get; set; }
}
