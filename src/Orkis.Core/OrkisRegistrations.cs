namespace Orkis;

/// <summary>
/// The keys registered through the Orkis DI helpers. Keyed services cannot be
/// enumerated from the container, so the helpers record their keys here — this is
/// what lets a host's introspection surface (a capabilities endpoint, a UI picker)
/// enumerate supervisors and models instead of hardcoding them.
/// </summary>
public sealed class OrkisRegistrations
{
    /// <summary>Keys registered via <c>AddOrkisSupervisor</c>, in registration order.</summary>
    public IList<string> SupervisorKeys { get; } = [];

    /// <summary>Keys registered via <c>AddOrkisChatClient</c>, in registration order.</summary>
    public IList<string> ModelKeys { get; } = [];
}
