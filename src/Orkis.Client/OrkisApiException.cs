using System.Net;

namespace Orkis.Client;

/// <summary>An error response from the daemon.</summary>
public sealed class OrkisApiException : Exception
{
    public OrkisApiException(HttpStatusCode statusCode, string message)
        : base(message) => StatusCode = statusCode;

    /// <summary>The HTTP status the daemon answered with.</summary>
    public HttpStatusCode StatusCode { get; }
}
