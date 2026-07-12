namespace Orkis.Sandboxing;

/// <summary>
/// The execution capabilities a supervision decision granted to one tool call:
/// a minimum isolation level and, optionally, network reach. These are facets of one
/// trust lattice — granted per action by a supervisor and auditable, never ambient.
/// </summary>
public sealed record ExecutionGrant
{
    /// <summary>
    /// Minimum sandbox level the call must execute under, or <see langword="null"/>
    /// to leave isolation to the tool's own configuration.
    /// </summary>
    public SandboxLevel? MinimumSandboxLevel { get; init; }

    /// <summary>
    /// Network reach granted to this execution, or <see langword="null"/> for the
    /// sandbox's configured default. Honored by sandboxes that control the network
    /// (Firecracker); namespace- and process-based sandboxes cannot enforce it.
    /// </summary>
    public NetworkMode? Network { get; init; }
}
