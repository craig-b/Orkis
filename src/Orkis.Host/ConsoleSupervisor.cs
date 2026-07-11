using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Host;

/// <summary>
/// Human-in-the-loop supervision at the console: read-only tools pass silently,
/// anything else asks the operator, who can also demand sandboxed execution.
/// </summary>
public sealed class ConsoleSupervisor : ISupervisor
{
    public Task<SupervisionDecision> ReviewAsync(ProposedAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (action.Tool.Risk == ToolRisk.ReadOnly)
        {
            return Task.FromResult(SupervisionDecision.Approve());
        }

        Console.WriteLine();
        Console.WriteLine($"┌─ supervision ─ tool '{action.Call.ToolName}' (risk: {action.Tool.Risk})");
        Console.WriteLine($"│  arguments: {action.Call.Arguments.GetRawText()}");
        Console.Write("└─ approve? [y]es / [s]andboxed / anything else denies: ");

        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        var decision = answer switch
        {
            "y" or "yes" => SupervisionDecision.Approve(),
            "s" or "sandboxed" => SupervisionDecision.Approve(Orkis.Sandboxing.SandboxLevel.Standard),
            _ => SupervisionDecision.Deny("The operator denied this action."),
        };
        return Task.FromResult(decision);
    }
}
