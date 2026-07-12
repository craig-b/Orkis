using System.Globalization;
using System.Threading.Channels;
using Orkis.Client;
using Orkis.Runs;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Orkis.Cli;

/// <summary>
/// The <c>orkis dash</c> live view: runs, pending approvals, and a rolling event
/// feed across every run, driven by the daemon-wide event stream on top of
/// <c>/v1/runs</c> and <c>/v1/approvals</c> snapshots. Keys: <c>a</c> decides
/// pending approvals, <c>q</c> quits.
/// </summary>
internal static class Dashboard
{
    private const int FeedLength = 12;
    private const int RunRows = 8;
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(2);

    public static async Task<int> RunAsync(OrkisClient client, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[red]error:[/] the dashboard needs an interactive terminal.");
            return 1;
        }

        var feed = new List<string>();
        var connected = true;

        // The pump owns the SSE connection and reconnects forever; the render loop
        // drains its channel. State-changing events also trigger a fresh snapshot.
        var events = Channel.CreateUnbounded<RunEvent>();
        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = PumpEventsAsync(client, events.Writer, state => connected = state, pumpCancellation.Token);

        IReadOnlyList<RunResponse> runs = [];
        IReadOnlyList<ApprovalResponse> approvals = [];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (runs, approvals) = await SnapshotAsync(client, runs, approvals, cancellationToken);

                var action = DashAction.None;
                await AnsiConsole
                    .Live(Render(runs, approvals, feed, connected))
                    .StartAsync(async live =>
                    {
                        var lastSnapshot = DateTime.UtcNow;
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            var dirty = DrainFeed(events.Reader, feed);
                            if (dirty || DateTime.UtcNow - lastSnapshot > SnapshotInterval)
                            {
                                (runs, approvals) = await SnapshotAsync(client, runs, approvals, cancellationToken);
                                lastSnapshot = DateTime.UtcNow;
                            }

                            live.UpdateTarget(Render(runs, approvals, feed, connected));

                            while (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(intercept: true).Key;
                                if (key == ConsoleKey.Q)
                                {
                                    action = DashAction.Quit;
                                    return;
                                }

                                if (key == ConsoleKey.A)
                                {
                                    action = DashAction.DecideApprovals;
                                    return;
                                }
                            }

                            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
                        }
                    });

                if (action != DashAction.DecideApprovals)
                {
                    return 0;
                }

                // The live display is stopped while prompting, then rebuilt.
                await DecideApprovalsAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl-C.
        }
        finally
        {
            pumpCancellation.Cancel();
            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }

        return 0;
    }

    private enum DashAction
    {
        None,
        Quit,
        DecideApprovals,
    }

    private static async Task PumpEventsAsync(
        OrkisClient client,
        ChannelWriter<RunEvent> writer,
        Action<bool> setConnected,
        CancellationToken cancellationToken
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                setConnected(true);
                await foreach (var runEvent in client.StreamAllEventsAsync(cancellationToken))
                {
                    writer.TryWrite(runEvent);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OrkisApiException or IOException)
            {
                // Daemon unreachable; keep trying so the dash survives restarts.
            }

            setConnected(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static bool DrainFeed(ChannelReader<RunEvent> reader, List<string> feed)
    {
        var drained = false;
        while (reader.TryRead(out var runEvent))
        {
            drained = true;
            feed.Add($"[grey]{ShortId(runEvent.RunId)}[/] {EventRenderer.Markup(runEvent)}");
            if (feed.Count > FeedLength)
            {
                feed.RemoveAt(0);
            }
        }

        return drained;
    }

    private static async Task<(IReadOnlyList<RunResponse>, IReadOnlyList<ApprovalResponse>)> SnapshotAsync(
        OrkisClient client,
        IReadOnlyList<RunResponse> previousRuns,
        IReadOnlyList<ApprovalResponse> previousApprovals,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return (
                await client.ListRunsAsync(cancellationToken),
                await client.ListApprovalsAsync(cancellationToken: cancellationToken)
            );
        }
        catch (Exception ex) when (ex is HttpRequestException or OrkisApiException)
        {
            // Keep showing the last known state while the daemon is unreachable.
            return (previousRuns, previousApprovals);
        }
    }

    private static Rows Render(
        IReadOnlyList<RunResponse> runs,
        IReadOnlyList<ApprovalResponse> approvals,
        List<string> feed,
        bool connected
    )
    {
        var status = connected ? "[green]connected[/]" : "[red]reconnecting…[/]";
        var rows = new List<IRenderable>
        {
            new Rule($"[bold]orkis dash[/] · {status} · [dim]a[/] decide approvals · [dim]q[/] quit")
            {
                Justification = Justify.Left,
            },
            RunsTable(runs),
        };

        if (approvals.Count > 0)
        {
            rows.Add(ApprovalsTable(approvals));
        }

        rows.Add(
            new Panel(new Markup(feed.Count > 0 ? string.Join("\n", feed) : "[dim]no events yet[/]"))
            {
                Header = new PanelHeader("events"),
                Expand = true,
            }
        );

        return new Rows(rows);
    }

    private static Table RunsTable(IReadOnlyList<RunResponse> runs)
    {
        var table = new Table { Expand = true }.AddColumns(
            "RUN",
            "STATUS",
            "ACTIVE",
            "SUPERVISOR",
            "TOKENS",
            "TOOLS",
            "UPDATED"
        );
        foreach (var run in runs.Take(RunRows))
        {
            table.AddRow(
                ShortId(run.RunId),
                StatusMarkup(run),
                run.Active ? "[green]yes[/]" : "[dim]no[/]",
                run.SupervisorKey?.EscapeMarkup() ?? "?",
                $"{run.InputTokens}/{run.OutputTokens}",
                run.ToolCalls.ToString(CultureInfo.InvariantCulture),
                run.UpdatedAt?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "-"
            );
        }

        if (runs.Count == 0)
        {
            table.AddRow("[dim]no runs yet[/]", "", "", "", "", "", "");
        }

        return table;
    }

    private static Table ApprovalsTable(IReadOnlyList<ApprovalResponse> approvals)
    {
        var table = new Table { Expand = true }.AddColumns("PENDING APPROVAL", "TOOL", "RISK", "ARGUMENTS");
        foreach (var approval in approvals)
        {
            var arguments = approval.Arguments.GetRawText().ReplaceLineEndings(" ");
            table.AddRow(
                $"[yellow]{ShortId(approval.RunId)}/{approval.CallId.EscapeMarkup()}[/]",
                approval.ToolName.EscapeMarkup(),
                approval.Risk.ToString(),
                (arguments.Length <= 60 ? arguments : arguments[..60] + "…").EscapeMarkup()
            );
        }

        return table;
    }

    private static string StatusMarkup(RunResponse run) =>
        run.Status switch
        {
            Orkis.Agents.RunStatus.Completed => "[green]completed[/]",
            Orkis.Agents.RunStatus.AwaitingSupervision => "[yellow]awaiting supervision[/]",
            Orkis.Agents.RunStatus.BudgetExceeded => "[red]budget exceeded[/]",
            _ => "running",
        };

    /// <summary>
    /// Prompts for every pending approval, then resumes each run whose decisions are
    /// complete. Runs with the live display stopped.
    /// </summary>
    private static async Task DecideApprovalsAsync(OrkisClient client, CancellationToken cancellationToken)
    {
        var pending = await client.ListApprovalsAsync(cancellationToken: cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        var decidedRuns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var approval in pending)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]{ShortId(approval.RunId)}[/] [bold]{approval.ToolName.EscapeMarkup()}[/] "
                    + $"[dim](risk: {approval.Risk})[/]"
            );
            AnsiConsole.MarkupLine($"  {approval.Arguments.GetRawText().ReplaceLineEndings(" ").EscapeMarkup()}");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("decision:")
                    .AddChoices(
                        "approve (host)",
                        "approve (sandboxed)",
                        "approve (sandboxed + network egress)",
                        "deny",
                        "skip"
                    )
            );
            var decision = choice switch
            {
                "approve (host)" => new DecideApprovalRequest { Verdict = "approve" },
                "approve (sandboxed)" => new DecideApprovalRequest { Verdict = "approve", SandboxLevel = "standard" },
                "approve (sandboxed + network egress)" => new DecideApprovalRequest
                {
                    Verdict = "approve",
                    SandboxLevel = "standard",
                    Network = "restrictedEgress",
                },
                "deny" => new DecideApprovalRequest { Verdict = "deny", Reason = "The operator denied this action." },
                _ => null,
            };
            if (decision is null)
            {
                continue;
            }

            await client.DecideApprovalAsync(approval.RunId, approval.CallId, decision, cancellationToken);
            decidedRuns.Add(approval.RunId);
        }

        foreach (var runId in decidedRuns)
        {
            var stillPending = await client.ListApprovalsAsync(runId, cancellationToken);
            if (stillPending.Count > 0)
            {
                continue;
            }

            try
            {
                await client.ResumeRunAsync(runId, cancellationToken);
                AnsiConsole.MarkupLine($"[blue]resumed[/] {ShortId(runId)}");
            }
            catch (OrkisApiException ex)
            {
                AnsiConsole.MarkupLine($"[dim]{ShortId(runId)}: {ex.Message.EscapeMarkup()}[/]");
            }
        }
    }

    private static string ShortId(string runId) => (runId.Length <= 8 ? runId : runId[^8..]).EscapeMarkup();
}
