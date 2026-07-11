namespace Orkis.Agents;

/// <summary>
/// Reusable system-prompt fragments encoding prompt-level policies. Hosts append the
/// ones they want to their own system prompt; nothing is injected automatically, so a
/// run's transcript never contains text the host did not choose.
/// </summary>
public static class SystemPromptFragments
{
    /// <summary>
    /// A guardrail against confabulation: the model must not present remembered or
    /// invented content as if it were tool output. Models asked to "read the file" or
    /// "run the command" will otherwise sometimes answer from memory of similar
    /// content, in exactly the register of a real result.
    /// </summary>
    public const string ConfabulationGuardrail =
        "Ground every factual claim in the actual tool results of this conversation. "
        + "Never present remembered, assumed, or invented content as if a tool returned it: "
        + "do not quote file contents you have not read here, output of commands you did not "
        + "execute, or data from sources you did not query. If something is unverified, say "
        + "so plainly instead of fabricating output. When a tool fails or returns nothing, "
        + "report that outcome rather than filling the gap from memory.";
}
