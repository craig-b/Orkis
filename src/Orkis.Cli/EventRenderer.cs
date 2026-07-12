using System.Globalization;
using Orkis.Client;
using Orkis.Runs;
using Spectre.Console;

namespace Orkis.Cli;

/// <summary>Renders run events as human-readable console lines.</summary>
internal static class EventRenderer
{
    public static void Render(RunEvent runEvent) =>
        AnsiConsole.MarkupLine($"[grey]{runEvent.Sequence, 3}[/] {Markup(runEvent)}");

    /// <summary>The event's one-line markup, without sequence or run prefix.</summary>
    public static string Markup(RunEvent runEvent)
    {
        var line = runEvent switch
        {
            RunStartedEvent e => $"[bold]run started[/] (supervision: {e.SupervisorKey.EscapeMarkup()})",
            RunResumedEvent => "[blue]run resumed[/]",
            ModelCallCompletedEvent e => $"[dim]model {(e.ModelId ?? "?").EscapeMarkup()}: "
                + $"{e.InputTokens} in / {e.OutputTokens} out tokens"
                + (e.Cost > 0 ? $", cost {e.Cost.ToString("0.####", CultureInfo.InvariantCulture)}" : "")
                + "[/]",
            ToolCallProposedEvent e => $"[yellow]→ {e.ToolName.EscapeMarkup()}[/] "
                + Truncate(CollapseWhitespace(e.ArgumentsJson), 100),
            SupervisionDecidedEvent e => RenderDecision(e),
            ToolCallCompletedEvent e => (e.IsError ? "[red]✗[/] " : "[green]✓[/] ")
                + $"{e.ToolName.EscapeMarkup()} [dim]({e.ContentLength} chars)[/] "
                + Truncate(e.ContentPreview.ReplaceLineEndings(" "), 120),
            RunPausedEvent => "[yellow]run paused awaiting supervision[/]",
            RunCompletedEvent e => RenderCompleted(e),
            UnknownRunEvent e => $"[grey]unknown event '{e.Type.EscapeMarkup()}' "
                + "(newer daemon? upgrade this client to see it)[/]",
            _ => $"[grey]{runEvent.GetType().Name.EscapeMarkup()}[/]",
        };
        return line;
    }

    private static string RenderDecision(SupervisionDecidedEvent e)
    {
        var verdict = e.Verdict.ToUpperInvariant() switch
        {
            "APPROVED" => "[green]approved[/]",
            "DENIED" => "[red]denied[/]",
            _ => $"[yellow]{e.Verdict.ToLowerInvariant().EscapeMarkup()}[/]",
        };
        var grants = new List<string>();
        if (e.RequiredSandboxLevel is not null)
        {
            grants.Add($"sandbox ≥ {e.RequiredSandboxLevel}");
        }

        if (e.GrantedNetwork is not null)
        {
            grants.Add($"network {e.GrantedNetwork}");
        }

        return $"supervision: {e.ToolName.EscapeMarkup()} {verdict}"
            + (grants.Count > 0 ? $" [dim]({string.Join(", ", grants).EscapeMarkup()})[/]" : "")
            + (e.Reason is { Length: > 0 } reason ? $" [dim]— {reason.EscapeMarkup()}[/]" : "");
    }

    private static string RenderCompleted(RunCompletedEvent e)
    {
        var status = e.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
            ? "[green]run completed[/]"
            : $"[red]run ended: {e.Status.EscapeMarkup()}[/]";
        return $"{status} [dim]({e.InputTokens} in / {e.OutputTokens} out tokens, "
            + $"{e.ToolCalls} tool call(s), cost {e.Cost.ToString("0.####", CultureInfo.InvariantCulture)})[/]"
            + (e.FinalTextPreview is { Length: > 0 } text ? $"\n    {text.EscapeMarkup()}" : "");
    }

    private static string Truncate(string text, int max) =>
        (text.Length <= max ? text : text[..max] + "…").EscapeMarkup();

    /// <summary>Flattens pretty-printed JSON to one line for single-line rendering.</summary>
    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}
