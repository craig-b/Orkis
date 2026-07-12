using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Orkis.Artifacts;
using Orkis.Runs;

namespace Orkis.Client;

/// <summary>
/// A typed client for the Orkis daemon: plain JSON request/response for commands, and
/// a typed <see cref="RunEvent"/> stream over SSE. One instance per daemon endpoint.
/// </summary>
public sealed class OrkisClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = SseRunEvents.JsonOptions;

    private readonly HttpClient _http;

    /// <summary>
    /// Connects to the daemon at <paramref name="socketPath"/>, or the resolved
    /// default endpoint when omitted (see <see cref="OrkisEndpoint.ResolveSocketPath"/>).
    /// </summary>
    public OrkisClient(string? socketPath = null)
    {
        var resolved = OrkisEndpoint.ResolveSocketPath(socketPath);
        _http = new HttpClient(
            new SocketsHttpHandler
            {
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket
                            .ConnectAsync(new UnixDomainSocketEndPoint(resolved), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }

                    return new NetworkStream(socket, ownsSocket: true);
                },
            }
        )
        {
            // The authority is unused over a Unix socket; it just anchors relative URIs.
            BaseAddress = new Uri("http://orkis"),
        };
    }

    /// <summary>Checks the daemon is reachable and healthy.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http
                .GetAsync(new Uri("/v1/healthz", UriKind.Relative), cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>Starts a run; the daemon executes it in the background.</summary>
    public async Task<RunAcceptedResponse> StartRunAsync(
        StartRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _http
            .PostAsJsonAsync(new Uri("/v1/runs", UriKind.Relative), request, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<RunAcceptedResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>All runs the daemon knows, most recently updated first.</summary>
    public async Task<IReadOnlyList<RunResponse>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .GetAsync(new Uri("/v1/runs", UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<IReadOnlyList<RunResponse>>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The run's current state, or <see langword="null"/> when unknown.</summary>
    public async Task<RunResponse?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        using var response = await _http
            .GetAsync(new Uri($"/v1/runs/{Uri.EscapeDataString(runId)}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadAsync<RunResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumes a paused or interrupted run from its checkpoint.</summary>
    public async Task<RunAcceptedResponse> ResumeRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        using var response = await _http
            .PostAsync(
                new Uri($"/v1/runs/{Uri.EscapeDataString(runId)}/resume", UriKind.Relative),
                content: null,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadAsync<RunAcceptedResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pending approvals, optionally scoped to one run.</summary>
    public async Task<IReadOnlyList<ApprovalResponse>> ListApprovalsAsync(
        string? runId = null,
        CancellationToken cancellationToken = default
    )
    {
        var uri = runId is null ? "/v1/approvals" : $"/v1/approvals?runId={Uri.EscapeDataString(runId)}";
        using var response = await _http
            .GetAsync(new Uri(uri, UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<IReadOnlyList<ApprovalResponse>>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decides a pending approval. Decisions are immutable once made.</summary>
    public async Task DecideApprovalAsync(
        string runId,
        string callId,
        DecideApprovalRequest decision,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(decision);

        using var response = await _http
            .PostAsJsonAsync(
                new Uri(
                    $"/v1/approvals/{Uri.EscapeDataString(runId)}/{Uri.EscapeDataString(callId)}",
                    UriKind.Relative
                ),
                decision,
                JsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>All stored artifacts, oldest first.</summary>
    public async Task<IReadOnlyList<ArtifactInfo>> ListArtifactsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .GetAsync(new Uri("/v1/artifacts", UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<IReadOnlyList<ArtifactInfo>>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams the run's events: recorded history after <paramref name="afterSequence"/>
    /// first, then — when <paramref name="follow"/> — live events until the run
    /// completes. Unknown event types surface as <see cref="UnknownRunEvent"/>.
    /// </summary>
    public async IAsyncEnumerable<RunEvent> StreamEventsAsync(
        string runId,
        long afterSequence = -1,
        bool follow = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var uri = new Uri(
            $"/v1/runs/{Uri.EscapeDataString(runId)}/events?follow={(follow ? "true" : "false")}",
            UriKind.Relative
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (afterSequence >= 0)
        {
            request.Headers.Add("Last-Event-ID", afterSequence.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await foreach (var runEvent in SseRunEvents.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                yield return runEvent;
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return payload ?? throw new OrkisApiException(response.StatusCode, "The daemon returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message;
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            );
            message = document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString() ?? response.ReasonPhrase ?? "Unknown daemon error."
                : response.ReasonPhrase ?? "Unknown daemon error.";
        }
        catch (JsonException)
        {
            message = response.ReasonPhrase ?? "Unknown daemon error.";
        }

        throw new OrkisApiException(response.StatusCode, message);
    }
}
