using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Orkis.Retrieval;

/// <summary>
/// Reranks retrieval candidates with a chat model: the query and all candidate
/// passages go to the model in one listwise prompt, and the scores it returns define
/// the new order. Works with any <see cref="IChatClient"/>, so second-stage retrieval
/// needs no infrastructure beyond the model already powering the agent. Scores are on
/// the model's 0–10 scale; candidates the model fails to score sort last at 0.
/// </summary>
public sealed class ChatClientReranker : IReranker
{
    private const string ScoringInstructions =
        "You score passages for how well they answer a query. Respond with only a JSON "
        + "array containing one {\"index\": <passage number>, \"score\": <0 to 10>} object "
        + "per passage — no other text. Score 0 for irrelevant, 10 for a passage that "
        + "directly and completely answers the query.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IChatClient _chatClient;
    private readonly ChatClientRerankerOptions _options;

    public ChatClientReranker(IChatClient chatClient, IOptions<ChatClientRerankerOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        _chatClient = chatClient;
        _options = options?.Value ?? new ChatClientRerankerOptions();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Scored<Chunk>>> RerankAsync(
        string query,
        IReadOnlyList<Scored<Chunk>> candidates,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return [];
        }

        var response = await _chatClient
            .GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, ScoringInstructions),
                    new ChatMessage(ChatRole.User, BuildPrompt(query, candidates)),
                ],
                new ChatOptions { Temperature = 0 },
                cancellationToken
            )
            .ConfigureAwait(false);

        var scores = ParseScores(response.Text, candidates.Count);
        return
        [
            .. candidates
                .Select((candidate, i) => new Scored<Chunk>(candidate.Item, scores.GetValueOrDefault(i + 1)))
                .OrderByDescending(static scored => scored.Score),
        ];
    }

    private string BuildPrompt(string query, IReadOnlyList<Scored<Chunk>> candidates)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Query:");
        builder.AppendLine(query);
        builder.AppendLine();
        builder.AppendLine("Passages:");
        for (var i = 0; i < candidates.Count; i++)
        {
            var text = candidates[i].Item.Text;
            if (text.Length > _options.MaxPassageCharacters)
            {
                text = text[.._options.MaxPassageCharacters] + "…";
            }

            builder.Append('[').Append(i + 1).AppendLine("]");
            builder.AppendLine(text);
        }

        return builder.ToString();
    }

    private static Dictionary<int, double> ParseScores(string responseText, int candidateCount)
    {
        // Tolerate prose or markdown fences around the array; the array itself must parse.
        var start = responseText.IndexOf('[', StringComparison.Ordinal);
        var end = responseText.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException(
                $"The reranking model returned no JSON array of scores: {Excerpt(responseText)}"
            );
        }

        List<PassageScore>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<PassageScore>>(responseText[start..(end + 1)], JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The reranking model returned an unparseable score array: {Excerpt(responseText)}",
                ex
            );
        }

        var scores = new Dictionary<int, double>();
        foreach (var entry in parsed ?? [])
        {
            if (entry.Index >= 1 && entry.Index <= candidateCount)
            {
                scores.TryAdd(entry.Index, entry.Score);
            }
        }

        return scores;
    }

    private static string Excerpt(string text) =>
        text.Length <= 200 ? text : string.Create(CultureInfo.InvariantCulture, $"{text[..200]}…");

    private sealed record PassageScore(int Index, double Score);
}
