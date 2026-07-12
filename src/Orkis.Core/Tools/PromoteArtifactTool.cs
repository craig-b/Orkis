using System.Text.Json;
using Orkis.Artifacts;
using Orkis.Sandboxing;

namespace Orkis.Tools;

/// <summary>
/// Promotes a file from the workload's persistent workspace into the artifact store —
/// the only way files leave an isolation level. Orchestrator-mediated: the host reads
/// the bytes out of the workspace; sandboxed code never touches the store. Declared
/// mutating so supervision reviews every promotion — approving one is a trust decision
/// about that specific content.
/// </summary>
public sealed class PromoteArtifactTool(IEnumerable<ISandbox> sandboxes, IArtifactStore artifacts, string workspaceKey)
    : ITool
{
    private readonly IReadOnlyList<ISandbox> _sandboxes = [.. sandboxes];

    public ToolDescriptor Descriptor { get; } =
        new()
        {
            Name = "promote_artifact",
            Description =
                "Promotes a file from the persistent workspace into the artifact store, making it "
                + "available outside the sandbox. Artifacts are immutable: names must be new.",
            ParametersSchema = JsonDocument
                .Parse(
                    """
                    {"type":"object","properties":{"path":{"type":"string","description":"Path of the file inside the workspace."},"name":{"type":"string","description":"Artifact name; defaults to the file name."}},"required":["path"]}
                    """
                )
                .RootElement,
            Risk = ToolRisk.Mutating,
        };

    public async Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var path = toolCall.Arguments.TryGetProperty("path", out var pathProperty) ? pathProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Error(toolCall, "Missing required argument 'path'.");
        }

        var name = toolCall.Arguments.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name;

        var matches = new List<(SandboxLevel Level, Stream Content)>();
        try
        {
            foreach (var sandbox in _sandboxes)
            {
                if (sandbox is not IWorkspaceFileAccess access)
                {
                    continue;
                }

                var stream = await access
                    .ReadWorkspaceFileAsync(workspaceKey, path, cancellationToken)
                    .ConfigureAwait(false);
                if (stream is not null)
                {
                    matches.Add((sandbox.Level, stream));
                }
            }

            if (matches.Count == 0)
            {
                return Error(toolCall, $"File '{path}' was not found in any sandbox workspace.");
            }

            if (matches.Count > 1)
            {
                var levels = string.Join(", ", matches.Select(m => m.Level));
                return Error(
                    toolCall,
                    $"File '{path}' exists in multiple sandbox workspaces ({levels}); promotion is ambiguous."
                );
            }

            ArtifactInfo info;
            try
            {
                info = await artifacts.SaveAsync(name, matches[0].Content, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Error(toolCall, ex.Message);
            }

            return new ToolResult
            {
                ToolCallId = toolCall.Id,
                Content =
                    $"Promoted '{path}' from the {matches[0].Level} workspace "
                    + $"as artifact '{info.Name}' ({info.Length} bytes).",
            };
        }
        finally
        {
            foreach (var (_, content) in matches)
            {
                await content.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static ToolResult Error(ToolCall toolCall, string message) =>
        new()
        {
            ToolCallId = toolCall.Id,
            Content = message,
            IsError = true,
        };
}
