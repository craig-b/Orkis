using Orkis.Sandboxing;

namespace Orkis.Supervision;

/// <summary>
/// A supervisor's decision on a proposed action. Approval can carry capability
/// grants — a required sandbox level and network reach — letting the supervisor
/// decide not just whether an action runs but how strongly it is contained.
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

    /// <summary>
    /// Network reach granted to the action, or <see langword="null"/> for the
    /// sandbox's configured default. Never wider than the supervisor explicitly grants.
    /// </summary>
    public NetworkMode? GrantedNetwork { get; init; }

    /// <summary>Explanation of the decision, surfaced to the agent when denied.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Approves the action, optionally requiring a minimum sandbox level and
    /// optionally granting network reach.
    /// </summary>
    public static SupervisionDecision Approve(
        SandboxLevel? requiredSandboxLevel = null,
        NetworkMode? grantedNetwork = null
    ) =>
        new()
        {
            Verdict = SupervisionVerdict.Approved,
            RequiredSandboxLevel = requiredSandboxLevel,
            GrantedNetwork = grantedNetwork,
        };

    /// <summary>Denies the action with an explanation the agent can act on.</summary>
    public static SupervisionDecision Deny(string reason) =>
        new() { Verdict = SupervisionVerdict.Denied, Reason = reason };

    /// <summary>Defers the decision; the run pauses until one arrives.</summary>
    public static SupervisionDecision Defer() => new() { Verdict = SupervisionVerdict.Pending };
}
