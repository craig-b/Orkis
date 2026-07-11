using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Host;

/// <summary>
/// Human-in-the-loop supervision at the console: read-only tools pass silently,
/// anything else asks the operator to run it on the host (no isolation), inside an
/// isolation sandbox, or deny. The choice becomes the minimum isolation level the
/// tool must honor.
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
        Console.Write("└─ run on [h]ost (no isolation) / in [s]andbox / [d]eny: ");

        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        var decision = answer switch
        {
            // Host execution: no isolation requirement, so the tool runs at its weakest sandbox.
            "h" or "host" => SupervisionDecision.Approve(),
            // Sandboxed: require at least standard isolation; the tool picks the weakest that satisfies it.
            "s" or "sandbox" or "sandboxed" => SupervisionDecision.Approve(Orkis.Sandboxing.SandboxLevel.Standard),
            _ => SupervisionDecision.Deny("The operator denied this action."),
        };
        return Task.FromResult(decision);
    }
}
