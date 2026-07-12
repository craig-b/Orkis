namespace Orkis.Sandboxing;

/// <summary>How much network access a sandboxed execution is granted.</summary>
public enum NetworkMode
{
    /// <summary>No network device at all — the sandbox cannot reach anything. The safe default.</summary>
    None = 0,

    /// <summary>
    /// Egress to the public internet, with the host, private ranges (RFC1918), link-local,
    /// and cloud metadata addresses blocked. Enforced by host firewall rules provisioned
    /// once by <c>scripts/setup-firecracker-network.sh</c>.
    /// </summary>
    RestrictedEgress = 1,

    /// <summary>Egress only to an explicit set of domains. Not yet implemented.</summary>
    Allowlist = 2,
}

/// <summary>
/// The network access granted to a sandboxed execution.
/// </summary>
/// <remarks>
/// <see cref="NetworkMode.None"/> and <see cref="NetworkMode.RestrictedEgress"/> are
/// implemented by the Firecracker sandbox; <see cref="NetworkMode.Allowlist"/> is
/// scaffolding for a future SNI-filtering proxy. The longer-term intent is that a
/// supervisor grants network reach per run — as it already grants a required sandbox
/// level. Namespace- and process-based sandboxes currently share the host's network
/// and do not enforce this policy.
/// </remarks>
public sealed record NetworkPolicy
{
    /// <summary>No network access. The default.</summary>
    public static NetworkPolicy None { get; } = new();

    /// <summary>Public-internet egress only; the host and private ranges are unreachable.</summary>
    public static NetworkPolicy RestrictedEgress { get; } = new() { Mode = NetworkMode.RestrictedEgress };

    /// <summary>The access mode.</summary>
    public NetworkMode Mode { get; init; } = NetworkMode.None;

    /// <summary>Domains permitted when <see cref="Mode"/> is <see cref="NetworkMode.Allowlist"/>.</summary>
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];
}
