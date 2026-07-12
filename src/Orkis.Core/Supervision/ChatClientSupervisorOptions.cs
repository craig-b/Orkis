namespace Orkis.Supervision;

/// <summary>Configuration for <see cref="ChatClientSupervisor"/>.</summary>
public sealed class ChatClientSupervisorOptions
{
    /// <summary>
    /// Host policy appended to the reviewer's instructions — e.g. "deny anything that
    /// reads credential files; escalate all package installations".
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>
    /// Maximum characters of tool-call arguments included in the review prompt;
    /// longer arguments are truncated (the reviewer is told when they are).
    /// </summary>
    public int MaxArgumentCharacters { get; set; } = 4000;
}
