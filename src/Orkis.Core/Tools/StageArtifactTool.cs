using System.Text.Json;
using Orkis.Artifacts;
using Orkis.Sandboxing;

namespace Orkis.Tools;

/// <summary>
/// Copies an artifact from the artifact store into the workload's persistent
/// workspaces, so sandboxed commands can read it. The inverse of
/// <see cref="PromoteArtifactTool"/>, and equally orchestrator-mediated. Declared
/// mutating so supervision reviews staging — the future confidentiality gate: putting
/// an artifact where low-trust code can read it is itself a decision.
/// </summary>
public sealed class StageArtifactTool(IEnumerable<ISandbox> sandboxes, IArtifactStore artifacts, string workspaceKey)
    : ITool
{
    private readonly IReadOnlyList<ISandbox> _sandboxes = [.. sandboxes];

    public ToolDescriptor Descriptor { get; } =
        new()
        {
            Name = "stage_artifact",
            Description =
                "Copies an artifact from the artifact store into the persistent workspace, "
                + "so shell commands can read it.",
            ParametersSchema = JsonDocument
                .Parse(
                    """
                    {"type":"object","properties":{"name":{"type":"string","description":"Name of the artifact to stage."},"path":{"type":"string","description":"Destination path inside the workspace; defaults to the artifact name."}},"required":["name"]}
                    """
                )
                .RootElement,
            Risk = ToolRisk.Mutating,
        };

    public async Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var name = toolCall.Arguments.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return Error(toolCall, "Missing required argument 'name'.");
        }

        var path = toolCall.Arguments.TryGetProperty("path", out var pathProperty) ? pathProperty.GetString() : null;
        path = string.IsNullOrWhiteSpace(path) ? name : path;

        byte[] bytes;
        var content = await artifacts.OpenAsync(name, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return Error(toolCall, $"No artifact named '{name}' exists.");
        }

        await using (content.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        var staged = new List<SandboxLevel>();
        foreach (var sandbox in _sandboxes)
        {
            if (sandbox is not IWorkspaceFileAccess access)
            {
                continue;
            }

            using var source = new MemoryStream(bytes, writable: false);
            await access.WriteWorkspaceFileAsync(workspaceKey, path, source, cancellationToken).ConfigureAwait(false);
            staged.Add(sandbox.Level);
        }

        if (staged.Count == 0)
        {
            return Error(toolCall, "No registered sandbox supports workspace file access.");
        }

        return new ToolResult
        {
            ToolCallId = toolCall.Id,
            Content =
                $"Staged artifact '{name}' ({bytes.Length} bytes) at '{path}' "
                + $"in the {string.Join(", ", staged)} workspace(s).",
        };
    }

    private static ToolResult Error(ToolCall toolCall, string message) =>
        new()
        {
            ToolCallId = toolCall.Id,
            Content = message,
            IsError = true,
        };
}
