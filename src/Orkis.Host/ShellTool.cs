using System.Text.Json;
using Orkis.Sandboxing;
using Orkis.Tools;

namespace Orkis.Host;

/// <summary>
/// Runs a shell command. Given the supervisor's required minimum isolation, it selects
/// the weakest available sandbox that satisfies it — so a plain approval (no requirement)
/// runs at the weakest sandbox registered (host execution when a <see cref="SandboxLevel.None"/>
/// sandbox is present), while a supervisor requiring isolation forces a stronger one.
/// Declared destructive, and implements <see cref="ISandboxedTool"/> so the requirement reaches it.
/// </summary>
public sealed class ShellTool(IEnumerable<ISandbox> sandboxes, string? workspaceKey = null) : ISandboxedTool
{
    private readonly IReadOnlyList<ISandbox> _sandboxes = [.. sandboxes];

    public ToolDescriptor Descriptor { get; } =
        new()
        {
            Name = "run_shell_command",
            Description =
                "Runs a shell command and returns its output. Depending on supervision, the command "
                + "runs either directly on the host or inside an isolation sandbox.",
            ParametersSchema = JsonDocument
                .Parse(
                    """
                    {"type":"object","properties":{"command":{"type":"string","description":"The shell command to run."}},"required":["command"]}
                    """
                )
                .RootElement,
            Risk = ToolRisk.Destructive,
        };

    public Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default) =>
        RunAsync(toolCall, SandboxLevel.None, cancellationToken);

    public Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        SandboxLevel minimumLevel,
        CancellationToken cancellationToken = default
    ) => RunAsync(toolCall, minimumLevel, cancellationToken);

    private async Task<ToolResult> RunAsync(
        ToolCall toolCall,
        SandboxLevel minimumLevel,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var sandbox = _sandboxes.Where(s => s.Level >= minimumLevel).OrderBy(s => s.Level).FirstOrDefault();
        if (sandbox is null)
        {
            return Error(toolCall, $"No registered sandbox provides at least isolation level {minimumLevel}.");
        }

        var command = toolCall.Arguments.TryGetProperty("command", out var property) ? property.GetString() : null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return Error(toolCall, "Missing required argument 'command'.");
        }

        var execution = await sandbox
            .ExecuteAsync(
                new SandboxExecutionRequest
                {
                    Executable = "/bin/sh",
                    Arguments = ["-c", command],
                    Timeout = TimeSpan.FromSeconds(30),
                    WorkspaceKey = workspaceKey,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        var environment = sandbox.Level == SandboxLevel.None ? "None (host, no isolation)" : sandbox.Level.ToString();
        var output =
            $"ran in: {environment}\n"
            + $"exit code: {execution.ExitCode}{(execution.TimedOut ? " (timed out)" : "")}\n"
            + $"stdout:\n{execution.StandardOutput}\n"
            + $"stderr:\n{execution.StandardError}";

        return new ToolResult
        {
            ToolCallId = toolCall.Id,
            Content = output,
            IsError = execution.ExitCode != 0 || execution.TimedOut,
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
