using System.CommandLine;
using System.Globalization;
using Orkis.Cli;
using Orkis.Client;
using Orkis.Runs;
using Spectre.Console;

var socketOption = new Option<string?>("--socket")
{
    Description = "The daemon's Unix socket (default: ORKIS_SOCKET, then the well-known path).",
    Recursive = true,
};

var root = new RootCommand("Thin client for the Orkis daemon.");
root.Options.Add(socketOption);

// run --------------------------------------------------------------------------
var promptArgument = new Argument<string>("prompt") { Description = "The prompt the agent should act on." };
var supervisorOption = new Option<string?>("--supervisor")
{
    Description = "Supervisor key for the run: queue (default), yolo, or ai.",
};
var modelOption = new Option<string?>("--model")
{
    Description = "Registered model key for the run (default: the daemon's default model).",
};
var systemOption = new Option<string?>("--system") { Description = "Optional system prompt." };
var maxTokensOption = new Option<long?>("--max-tokens") { Description = "Token budget for the run." };
var maxToolCallsOption = new Option<int?>("--max-tool-calls") { Description = "Tool-call budget for the run." };
var detachOption = new Option<bool>("--detach", "-d")
{
    Description = "Start the run and print its id instead of attaching.",
};

var runCommand = new Command("run", "Start a run and attach to its event stream.");
runCommand.Arguments.Add(promptArgument);
runCommand.Options.Add(supervisorOption);
runCommand.Options.Add(modelOption);
runCommand.Options.Add(systemOption);
runCommand.Options.Add(maxTokensOption);
runCommand.Options.Add(maxToolCallsOption);
runCommand.Options.Add(detachOption);
runCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                var accepted = await client.StartRunAsync(
                    new StartRunRequest
                    {
                        Prompt = parseResult.GetValue(promptArgument)!,
                        SystemPrompt = parseResult.GetValue(systemOption),
                        SupervisorKey = parseResult.GetValue(supervisorOption),
                        Model = parseResult.GetValue(modelOption),
                        MaxTokens = parseResult.GetValue(maxTokensOption),
                        MaxToolCalls = parseResult.GetValue(maxToolCallsOption),
                    },
                    cancellationToken
                );

                if (parseResult.GetValue(detachOption))
                {
                    Console.WriteLine(accepted.RunId);
                    return 0;
                }

                AnsiConsole.MarkupLine($"[bold]run {accepted.RunId.EscapeMarkup()}[/]");
                return await AttachAsync(client, accepted.RunId, cancellationToken);
            }
        )
);
root.Subcommands.Add(runCommand);

// ps ---------------------------------------------------------------------------
var psCommand = new Command("ps", "List runs.");
psCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                var runs = await client.ListRunsAsync(cancellationToken);
                var table = new Table().AddColumns(
                    "RUN ID",
                    "STATUS",
                    "ACTIVE",
                    "SUPERVISOR",
                    "TOKENS",
                    "TOOLS",
                    "UPDATED"
                );
                foreach (var run in runs)
                {
                    table.AddRow(
                        run.RunId.EscapeMarkup(),
                        run.Status.ToString(),
                        run.Active ? "[green]yes[/]" : "no",
                        run.SupervisorKey?.EscapeMarkup() ?? "?",
                        $"{run.InputTokens}/{run.OutputTokens}",
                        run.ToolCalls.ToString(CultureInfo.InvariantCulture),
                        run.UpdatedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "-"
                    );
                }

                AnsiConsole.Write(table);
                return 0;
            }
        )
);
root.Subcommands.Add(psCommand);

// logs -------------------------------------------------------------------------
var runIdArgument = new Argument<string>("run-id") { Description = "The run to operate on." };
var followOption = new Option<bool>("--follow", "-f") { Description = "Keep streaming live events." };
var afterOption = new Option<long>("--after")
{
    Description = "Replay only events after this sequence number.",
    DefaultValueFactory = _ => -1,
};

var logsCommand = new Command("logs", "Show a run's event history.");
logsCommand.Arguments.Add(runIdArgument);
logsCommand.Options.Add(followOption);
logsCommand.Options.Add(afterOption);
logsCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                var events = client.StreamEventsAsync(
                    parseResult.GetValue(runIdArgument)!,
                    parseResult.GetValue(afterOption),
                    parseResult.GetValue(followOption),
                    cancellationToken
                );
                await foreach (var runEvent in events)
                {
                    EventRenderer.Render(runEvent);
                }

                return 0;
            }
        )
);
root.Subcommands.Add(logsCommand);

// resume -----------------------------------------------------------------------
var resumeCommand = new Command("resume", "Resume a paused or interrupted run.");
resumeCommand.Arguments.Add(runIdArgument);
resumeCommand.Options.Add(followOption);
resumeCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                var runId = parseResult.GetValue(runIdArgument)!;
                await client.ResumeRunAsync(runId, cancellationToken);
                if (!parseResult.GetValue(followOption))
                {
                    Console.WriteLine(runId);
                    return 0;
                }

                return await AttachAsync(client, runId, cancellationToken);
            }
        )
);
root.Subcommands.Add(resumeCommand);

// approvals --------------------------------------------------------------------
var approvalsRunOption = new Option<string?>("--run") { Description = "Only approvals for this run." };
var approvalsCommand = new Command("approvals", "List pending approvals.");
approvalsCommand.Options.Add(approvalsRunOption);
approvalsCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                var approvals = await client.ListApprovalsAsync(
                    parseResult.GetValue(approvalsRunOption),
                    cancellationToken
                );
                if (approvals.Count == 0)
                {
                    Console.WriteLine("no pending approvals");
                    return 0;
                }

                var table = new Table().AddColumns("RUN ID", "CALL ID", "TOOL", "RISK", "REQUESTED", "ARGUMENTS");
                foreach (var approval in approvals)
                {
                    var arguments = approval.Arguments.GetRawText().ReplaceLineEndings(" ");
                    table.AddRow(
                        approval.RunId.EscapeMarkup(),
                        approval.CallId.EscapeMarkup(),
                        approval.ToolName.EscapeMarkup(),
                        approval.Risk.ToString(),
                        approval.RequestedAt.ToString("u", CultureInfo.InvariantCulture),
                        (arguments.Length <= 80 ? arguments : arguments[..80] + "…").EscapeMarkup()
                    );
                }

                AnsiConsole.Write(table);
                return 0;
            }
        )
);
root.Subcommands.Add(approvalsCommand);

// approve / deny ---------------------------------------------------------------
var callIdArgument = new Argument<string>("call-id") { Description = "The pending tool call's id." };
var sandboxOption = new Option<string?>("--sandbox")
{
    Description = "Require a minimum sandbox level: none, standard, or strict.",
};
var networkOption = new Option<string?>("--network") { Description = "Grant network reach: none or restrictedEgress." };
var reasonOption = new Option<string?>("--reason") { Description = "Reason, surfaced to the agent when denied." };
var alsoResumeOption = new Option<bool>("--resume") { Description = "Resume the run after deciding." };

var approveCommand = new Command("approve", "Approve a pending tool call.");
approveCommand.Arguments.Add(runIdArgument);
approveCommand.Arguments.Add(callIdArgument);
approveCommand.Options.Add(sandboxOption);
approveCommand.Options.Add(networkOption);
approveCommand.Options.Add(alsoResumeOption);
approveCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            client =>
                DecideAsync(
                    client,
                    parseResult.GetValue(runIdArgument)!,
                    parseResult.GetValue(callIdArgument)!,
                    new DecideApprovalRequest
                    {
                        Verdict = "approve",
                        SandboxLevel = parseResult.GetValue(sandboxOption),
                        Network = parseResult.GetValue(networkOption),
                    },
                    parseResult.GetValue(alsoResumeOption),
                    cancellationToken
                )
        )
);
root.Subcommands.Add(approveCommand);

var denyCommand = new Command("deny", "Deny a pending tool call.");
denyCommand.Arguments.Add(runIdArgument);
denyCommand.Arguments.Add(callIdArgument);
denyCommand.Options.Add(reasonOption);
denyCommand.Options.Add(alsoResumeOption);
denyCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            client =>
                DecideAsync(
                    client,
                    parseResult.GetValue(runIdArgument)!,
                    parseResult.GetValue(callIdArgument)!,
                    new DecideApprovalRequest { Verdict = "deny", Reason = parseResult.GetValue(reasonOption) },
                    parseResult.GetValue(alsoResumeOption),
                    cancellationToken
                )
        )
);
root.Subcommands.Add(denyCommand);

// dash ---------------------------------------------------------------------------
var dashCommand = new Command("dash", "Live dashboard: runs, pending approvals, and the event feed.");
dashCommand.SetAction(
    (parseResult, cancellationToken) => WithClient(parseResult, client => Dashboard.RunAsync(client, cancellationToken))
);
root.Subcommands.Add(dashCommand);

// artifacts ----------------------------------------------------------------------
var artifactsCommand = new Command("artifacts", "List promoted artifacts.");
artifactsCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                var artifacts = await client.ListArtifactsAsync(cancellationToken);
                if (artifacts.Count == 0)
                {
                    Console.WriteLine("no artifacts");
                    return 0;
                }

                var table = new Table().AddColumns("NAME", "BYTES", "CREATED");
                foreach (var artifact in artifacts)
                {
                    table.AddRow(
                        artifact.Name.EscapeMarkup(),
                        artifact.Length.ToString(CultureInfo.InvariantCulture),
                        artifact.CreatedAt.ToString("u", CultureInfo.InvariantCulture)
                    );
                }

                AnsiConsole.Write(table);
                return 0;
            }
        )
);
root.Subcommands.Add(artifactsCommand);

return await root.Parse(args).InvokeAsync();

// ------------------------------------------------------------------------------

async Task<int> WithClient(ParseResult parseResult, Func<OrkisClient, Task<int>> action)
{
    using var client = new OrkisClient(parseResult.GetValue(socketOption));
    try
    {
        return await action(client);
    }
    catch (OrkisApiException ex)
    {
        AnsiConsole.MarkupLine($"[red]error:[/] {ex.Message.EscapeMarkup()}");
        return 1;
    }
    catch (HttpRequestException ex)
    {
        AnsiConsole.MarkupLine(
            $"[red]error:[/] cannot reach the daemon "
                + $"([dim]{OrkisEndpoint.ResolveSocketPath(parseResult.GetValue(socketOption)).EscapeMarkup()}[/]): "
                + ex.Message.EscapeMarkup()
        );
        AnsiConsole.MarkupLine("[dim]is it running? start it with: dotnet run --project src/Orkis.Daemon[/]");
        return 1;
    }
}

static async Task<int> DecideAsync(
    OrkisClient client,
    string runId,
    string callId,
    DecideApprovalRequest decision,
    bool alsoResume,
    CancellationToken cancellationToken
)
{
    await client.DecideApprovalAsync(runId, callId, decision, cancellationToken);
    Console.WriteLine($"{decision.Verdict}d: {callId}");
    if (alsoResume)
    {
        await client.ResumeRunAsync(runId, cancellationToken);
        Console.WriteLine($"resumed: {runId}");
    }

    return 0;
}

/// <summary>
/// Attaches to a run's live event stream, prompting for supervision decisions when it
/// pauses. Decisions and the resume go over the wire; the already-open stream then
/// carries the run's next events, so attachment survives the pause.
/// </summary>
static async Task<int> AttachAsync(OrkisClient client, string runId, CancellationToken cancellationToken)
{
    var completed = false;
    await foreach (var runEvent in client.StreamEventsAsync(runId, follow: true, cancellationToken: cancellationToken))
    {
        EventRenderer.Render(runEvent);
        switch (runEvent)
        {
            case RunCompletedEvent e:
                completed = e.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
                break;
            case RunPausedEvent when !await PromptDecisionsAsync(client, runId, cancellationToken):
                AnsiConsole.MarkupLine(
                    $"[yellow]left pending[/] — decide with [bold]orkis approvals[/], then "
                        + $"[bold]orkis resume {runId.EscapeMarkup()} -f[/]"
                );
                return 2;
        }
    }

    return completed ? 0 : 2;
}

/// <summary>
/// Prompts for each pending approval of the run and resumes it when every one is
/// decided. Returns <see langword="false"/> when the operator leaves decisions
/// pending (or the terminal is not interactive).
/// </summary>
static async Task<bool> PromptDecisionsAsync(OrkisClient client, string runId, CancellationToken cancellationToken)
{
    var pending = await client.ListApprovalsAsync(runId, cancellationToken);
    if (pending.Count == 0)
    {
        // Decided elsewhere (another client) between the pause event and now.
        await client.ResumeRunAsync(runId, cancellationToken);
        return true;
    }

    if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
    {
        return false;
    }

    foreach (var approval in pending)
    {
        AnsiConsole.MarkupLine(
            $"\n[yellow]approval needed:[/] [bold]{approval.ToolName.EscapeMarkup()}[/] "
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
                    "leave pending"
                )
        );
        switch (choice)
        {
            case "approve (host)":
                await client.DecideApprovalAsync(
                    approval.RunId,
                    approval.CallId,
                    new DecideApprovalRequest { Verdict = "approve" },
                    cancellationToken
                );
                break;
            case "approve (sandboxed)":
                await client.DecideApprovalAsync(
                    approval.RunId,
                    approval.CallId,
                    new DecideApprovalRequest { Verdict = "approve", SandboxLevel = "standard" },
                    cancellationToken
                );
                break;
            case "approve (sandboxed + network egress)":
                await client.DecideApprovalAsync(
                    approval.RunId,
                    approval.CallId,
                    new DecideApprovalRequest
                    {
                        Verdict = "approve",
                        SandboxLevel = "standard",
                        Network = "restrictedEgress",
                    },
                    cancellationToken
                );
                break;
            case "deny":
                await client.DecideApprovalAsync(
                    approval.RunId,
                    approval.CallId,
                    new DecideApprovalRequest { Verdict = "deny", Reason = "The operator denied this action." },
                    cancellationToken
                );
                break;
            default:
                return false;
        }
    }

    await client.ResumeRunAsync(runId, cancellationToken);
    return true;
}
