using System.Text.Json;
using Orkis.Sandboxing;
using Orkis.Tools;

namespace Orkis.Host;

/// <summary>
/// Runs a shell command inside the configured sandbox. Declared destructive, so
/// supervision policies treat it with appropriate suspicion; implements
/// <see cref="ISandboxedTool"/> so a supervisor can demand a minimum isolation level.
/// </summary>
public sealed class ShellTool(ISandbox sandbox) : ISandboxedTool
{
    public ToolDescriptor Descriptor { get; } =
        new()
        {
            Name = "run_shell_command",
            Description = "Runs a shell command and returns its output. The command executes in a sandbox.",
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
        InvokeAsync(toolCall, sandbox.Level, cancellationToken);

    public async Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        SandboxLevel minimumLevel,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (sandbox.Level < minimumLevel)
        {
            return new ToolResult
            {
                ToolCallId = toolCall.Id,
                Content = $"The configured sandbox provides level {sandbox.Level}, below the required {minimumLevel}.",
                IsError = true,
            };
        }

        var command = toolCall.Arguments.TryGetProperty("command", out var property) ? property.GetString() : null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ToolResult
            {
                ToolCallId = toolCall.Id,
                Content = "Missing required argument 'command'.",
                IsError = true,
            };
        }

        var execution = await sandbox
            .ExecuteAsync(
                new SandboxExecutionRequest
                {
                    Executable = "/bin/sh",
                    Arguments = ["-c", command],
                    Timeout = TimeSpan.FromSeconds(30),
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        var output =
            $"exit code: {execution.ExitCode}{(execution.TimedOut ? " (timed out)" : "")}\n"
            + $"stdout:\n{execution.StandardOutput}\n"
            + $"stderr:\n{execution.StandardError}";

        return new ToolResult
        {
            ToolCallId = toolCall.Id,
            Content = output,
            IsError = execution.ExitCode != 0 || execution.TimedOut,
        };
    }
}
