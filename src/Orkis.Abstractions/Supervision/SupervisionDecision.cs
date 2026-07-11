using Orkis.Sandboxing;

namespace Orkis.Supervision;

/// <summary>
/// A supervisor's decision on a proposed action. Approval can carry a required
/// sandbox level, letting the supervisor decide not just whether an action runs
/// but how strongly it must be isolated.
/// </summary>
public sealed record SupervisionDecision
{
    /// <summary>The verdict on the proposed action.</summary>
    public required SupervisionVerdict Verdict { get; init; }

    /// <summary>
    /// Minimum sandbox level the action must execute under, or <see langword="null"/>
    /// to leave the level to the tool's own configuration.
    /// </summary>
    public SandboxLevel? RequiredSandboxLevel { get; init; }

    /// <summary>Explanation of the decision, surfaced to the agent when denied.</summary>
    public string? Reason { get; init; }

    /// <summary>Approves the action, optionally requiring a minimum sandbox level.</summary>
    public static SupervisionDecision Approve(SandboxLevel? requiredSandboxLevel = null) =>
        new() { Verdict = SupervisionVerdict.Approved, RequiredSandboxLevel = requiredSandboxLevel };

    /// <summary>Denies the action with an explanation the agent can act on.</summary>
    public static SupervisionDecision Deny(string reason) =>
        new() { Verdict = SupervisionVerdict.Denied, Reason = reason };

    /// <summary>Defers the decision; the run pauses until one arrives.</summary>
    public static SupervisionDecision Defer() => new() { Verdict = SupervisionVerdict.Pending };
}
