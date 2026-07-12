namespace Orkis.Daemon;

/// <summary>
/// An additional chat model registered under a per-run key (see
/// <c>AgentRunRequest.ModelKey</c>), parsed from <c>ORKIS_MODELS</c>.
/// </summary>
public sealed record ModelRegistration(string Key, string Provider, string ModelId, string ApiKey);
