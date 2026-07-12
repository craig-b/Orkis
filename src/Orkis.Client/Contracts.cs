using System.Text.Json;
using Orkis.Agents;
using Orkis.Tools;

namespace Orkis.Client;

/// <summary>Body of <c>POST /v1/runs</c>.</summary>
public sealed record StartRunRequest
{
    /// <summary>The user prompt the agent should act on.</summary>
    public required string Prompt { get; init; }

    /// <summary>Optional system prompt establishing the agent's role and constraints.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Supervisor key for the run; defaults to the daemon's queue supervision.</summary>
    public string? SupervisorKey { get; init; }

    /// <summary>Registered model key for the run, or <see langword="null"/> for the daemon's default.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Makes the run a chat: turns end awaiting the next user message
    /// (<c>POST /v1/runs/{id}/messages</c>) instead of terminating.
    /// </summary>
    public bool Chat { get; init; }

    /// <summary>Optional token budget for the run.</summary>
    public long? MaxTokens { get; init; }

    /// <summary>Optional tool-call budget for the run.</summary>
    public int? MaxToolCalls { get; init; }
}

/// <summary>Response of <c>POST /v1/runs</c> and <c>POST /v1/runs/{id}/resume</c>.</summary>
public sealed record RunAcceptedResponse
{
    /// <summary>The run's identifier — the handle for status, events, and resume.</summary>
    public required string RunId { get; init; }
}

/// <summary>A run as reported by <c>GET /v1/runs</c> and <c>GET /v1/runs/{id}</c>.</summary>
public sealed record RunResponse
{
    public required string RunId { get; init; }

    /// <summary>The run's status at its latest checkpoint.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>Whether the run is executing in the daemon right now.</summary>
    public required bool Active { get; init; }

    public string? SupervisorKey { get; init; }

    /// <summary>Where the run came from (e.g. <c>schedule:&lt;id&gt;</c>), or null for a direct run.</summary>
    public string? Origin { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public decimal Cost { get; init; }

    public int ToolCalls { get; init; }

    /// <summary>When the latest checkpoint was written; <see langword="null"/> before the first one.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The error that ended the run's last segment in this daemon, if any.</summary>
    public string? LastError { get; init; }
}

/// <summary>What the daemon offers, as reported by <c>GET /v1/capabilities</c>.</summary>
public sealed record CapabilitiesResponse
{
    /// <summary>Registered supervisor keys, selectable per run.</summary>
    public required IReadOnlyList<string> Supervisors { get; init; }

    /// <summary>The supervisor used when a run selects none.</summary>
    public required string DefaultSupervisor { get; init; }

    /// <summary>Registered model keys, selectable per run.</summary>
    public required IReadOnlyList<string> Models { get; init; }

    /// <summary>Model id of the default chat client, or <see langword="null"/> offline.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>The configured isolation sandbox.</summary>
    public required string Sandbox { get; init; }

    /// <summary>Whether agent memory (save/search/recall) is on.</summary>
    public required bool Memory { get; init; }

    /// <summary>Whether corpus retrieval (search_corpus) is on.</summary>
    public required bool CorpusRetrieval { get; init; }

    /// <summary>Always-on tool names.</summary>
    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>Tool names discoverable through search_tools (e.g. MCP contributions).</summary>
    public required IReadOnlyList<string> CatalogTools { get; init; }
}

/// <summary>A pending approval as reported by <c>GET /v1/approvals</c>.</summary>
public sealed record ApprovalResponse
{
    public required string RunId { get; init; }

    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public required ToolRisk Risk { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>The proposed call's arguments as a JSON object.</summary>
    public required JsonElement Arguments { get; init; }
}

/// <summary>Body of <c>POST /v1/schedules</c>. A schedule is a cron-triggered run template.</summary>
public sealed record CreateScheduleRequest
{
    public required string Name { get; init; }

    /// <summary>Cron expression (Cronos syntax; a sixth field adds seconds).</summary>
    public required string Cron { get; init; }

    public required string Prompt { get; init; }

    public string? SupervisorKey { get; init; }

    public string? Model { get; init; }

    public IReadOnlyList<string>? ToolNames { get; init; }

    /// <summary><c>fresh</c>, <c>sharedStorage</c>, or <c>sharedStorageWithHandoff</c>.</summary>
    public string? Continuity { get; init; }

    public long? MaxTokens { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Body of <c>PATCH /v1/schedules/{id}</c>. Every field is optional; a null field leaves
/// that part of the schedule unchanged, so enable/disable is a one-field patch.
/// </summary>
public sealed record UpdateScheduleRequest
{
    public string? Name { get; init; }

    public string? Cron { get; init; }

    public string? Prompt { get; init; }

    public string? SupervisorKey { get; init; }

    public string? Model { get; init; }

    /// <summary><c>fresh</c>, <c>sharedStorage</c>, or <c>sharedStorageWithHandoff</c>.</summary>
    public string? Continuity { get; init; }

    public long? MaxTokens { get; init; }

    public bool? Enabled { get; init; }
}

/// <summary>A schedule as reported by <c>GET /v1/schedules</c>.</summary>
public sealed record ScheduleResponse
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Cron { get; init; }

    public required string Prompt { get; init; }

    public required string SupervisorKey { get; init; }

    public string? Model { get; init; }

    public required string Continuity { get; init; }

    public required bool Enabled { get; init; }

    public DateTimeOffset? LastFiredAt { get; init; }

    public string? LastRunId { get; init; }
}

/// <summary>Body of <c>POST /v1/runs/{id}/messages</c> — a chat's next user message.</summary>
public sealed record ContinueRunRequest
{
    /// <summary>The user's next message.</summary>
    public required string Message { get; init; }
}

/// <summary>Body of <c>POST /v1/mcp-servers</c> — connect an MCP server to the live daemon.</summary>
public sealed record AddMcpServerRequest
{
    /// <summary>An <c>http(s)://</c> Streamable HTTP endpoint or a stdio command line.</summary>
    public required string Server { get; init; }

    /// <summary>Optional name to register it under; defaults to the server's own name, made unique.</summary>
    public string? Name { get; init; }
}

/// <summary>A connected MCP server as reported by <c>GET /v1/mcp-servers</c>.</summary>
public sealed record McpServerResponse
{
    /// <summary>The name it is registered under (unique; used to remove it).</summary>
    public required string Name { get; init; }

    /// <summary>The configuration string it was connected from.</summary>
    public required string Server { get; init; }

    /// <summary>Names of the tools it contributes to the catalogue.</summary>
    public required IReadOnlyList<string> Tools { get; init; }
}

/// <summary>Body of <c>POST /v1/approvals/{runId}/{callId}</c>.</summary>
public sealed record DecideApprovalRequest
{
    /// <summary><c>approve</c> or <c>deny</c>.</summary>
    public required string Verdict { get; init; }

    /// <summary>Minimum sandbox level to require: <c>none</c>, <c>standard</c>, or <c>strict</c>.</summary>
    public string? SandboxLevel { get; init; }

    /// <summary>Network reach to grant: <c>none</c> or <c>restrictedEgress</c>.</summary>
    public string? Network { get; init; }

    /// <summary>Reason for the decision, surfaced to the agent when denied.</summary>
    public string? Reason { get; init; }
}
