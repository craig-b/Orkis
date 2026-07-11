namespace Orkis.Retrieval;

/// <summary>Configuration for <see cref="ChatClientReranker"/>.</summary>
public sealed class ChatClientRerankerOptions
{
    /// <summary>
    /// Maximum characters of each candidate passage included in the scoring prompt;
    /// longer passages are truncated. Bounds the prompt size when candidates are large.
    /// </summary>
    public int MaxPassageCharacters { get; set; } = 2000;
}
