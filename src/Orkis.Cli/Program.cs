using System.CommandLine;
using System.Globalization;
using Orkis.Cli;
using Orkis.Client;
using Orkis.Runs;
using Spectre.Console;

var socketOption = new Option<string?>("--socket")
{
    Description =
        "The daemon endpoint: a Unix socket path or an http(s):// URL "
        + "(default: ORKIS_HOST, then ORKIS_SOCKET, then the well-known path).",
    Recursive = true,
};
var tokenOption = new Option<string?>("--token")
{
    Description = "Bearer token for TCP endpoints (default: ORKIS_TOKEN).",
    Recursive = true,
};

var root = new RootCommand("Thin client for the Orkis daemon.");
root.Options.Add(socketOption);
root.Options.Add(tokenOption);

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

// chat -------------------------------------------------------------------------
var chatMessageArgument = new Argument<string?>("message")
{
    Description = "The first message; prompted for interactively when omitted.",
    Arity = ArgumentArity.ZeroOrOne,
};
var chatRunOption = new Option<string?>("--run") { Description = "Continue an existing chat by run id." };

var chatCommand = new Command("chat", "Start (or continue) an interactive multi-turn chat.");
chatCommand.Arguments.Add(chatMessageArgument);
chatCommand.Options.Add(chatRunOption);
chatCommand.Options.Add(supervisorOption);
chatCommand.Options.Add(modelOption);
chatCommand.Options.Add(systemOption);
chatCommand.Options.Add(maxTokensOption);
chatCommand.Options.Add(maxToolCallsOption);
chatCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            client =>
                ChatAsync(
                    client,
                    parseResult.GetValue(chatRunOption),
                    parseResult.GetValue(chatMessageArgument),
                    new StartRunRequest
                    {
                        Prompt = "", // filled in from the first message
                        Chat = true,
                        SystemPrompt = parseResult.GetValue(systemOption),
                        SupervisorKey = parseResult.GetValue(supervisorOption),
                        Model = parseResult.GetValue(modelOption),
                        MaxTokens = parseResult.GetValue(maxTokensOption),
                        MaxToolCalls = parseResult.GetValue(maxToolCallsOption),
                    },
                    cancellationToken
                )
        )
);
root.Subcommands.Add(chatCommand);

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

// schedules ----------------------------------------------------------------------
var schedulesCommand = new Command("schedules", "List, create, or remove scheduled runs.");
schedulesCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                var schedules = await client.ListSchedulesAsync(cancellationToken);
                if (schedules.Count == 0)
                {
                    Console.WriteLine("no schedules");
                    return 0;
                }

                var table = new Table().AddColumns(
                    "ID",
                    "NAME",
                    "CRON",
                    "SUPERVISOR",
                    "CONTINUITY",
                    "ENABLED",
                    "LAST FIRED"
                );
                foreach (var schedule in schedules)
                {
                    table.AddRow(
                        schedule.Id.EscapeMarkup(),
                        schedule.Name.EscapeMarkup(),
                        schedule.Cron.EscapeMarkup(),
                        schedule.SupervisorKey.EscapeMarkup(),
                        schedule.Continuity.EscapeMarkup(),
                        schedule.Enabled ? "yes" : "[dim]no[/]",
                        schedule.LastFiredAt?.ToString("u", CultureInfo.InvariantCulture) ?? "-"
                    );
                }

                AnsiConsole.Write(table);
                return 0;
            }
        )
);

var scheduleCronArgument = new Argument<string>("cron") { Description = "Cron expression (a 6th field adds seconds)." };
var schedulePromptArgument = new Argument<string>("prompt") { Description = "The prompt each firing runs." };
var scheduleNameOption = new Option<string?>("--name") { Description = "A name for the schedule." };
var scheduleContinuityOption = new Option<string?>("--continuity")
{
    Description = "fresh (default), sharedStorage, or sharedStorageWithHandoff.",
};
var addScheduleCommand = new Command("add", "Create a schedule.");
addScheduleCommand.Arguments.Add(scheduleCronArgument);
addScheduleCommand.Arguments.Add(schedulePromptArgument);
addScheduleCommand.Options.Add(scheduleNameOption);
addScheduleCommand.Options.Add(supervisorOption);
addScheduleCommand.Options.Add(modelOption);
addScheduleCommand.Options.Add(scheduleContinuityOption);
addScheduleCommand.Options.Add(maxTokensOption);
addScheduleCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                var cron = parseResult.GetValue(scheduleCronArgument)!;
                var prompt = parseResult.GetValue(schedulePromptArgument)!;
                var created = await client.CreateScheduleAsync(
                    new CreateScheduleRequest
                    {
                        Name = parseResult.GetValue(scheduleNameOption) ?? prompt,
                        Cron = cron,
                        Prompt = prompt,
                        SupervisorKey = parseResult.GetValue(supervisorOption),
                        Model = parseResult.GetValue(modelOption),
                        Continuity = parseResult.GetValue(scheduleContinuityOption),
                        MaxTokens = parseResult.GetValue(maxTokensOption),
                    },
                    cancellationToken
                );
                Console.WriteLine(created.Id);
                return 0;
            }
        )
);
schedulesCommand.Subcommands.Add(addScheduleCommand);

var scheduleIdArgument = new Argument<string>("id") { Description = "The schedule to remove." };
var removeScheduleCommand = new Command("rm", "Remove a schedule.");
removeScheduleCommand.Arguments.Add(scheduleIdArgument);
removeScheduleCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                var id = parseResult.GetValue(scheduleIdArgument)!;
                if (await client.DeleteScheduleAsync(id, cancellationToken))
                {
                    Console.WriteLine($"removed {id}");
                    return 0;
                }

                AnsiConsole.MarkupLine($"[red]error:[/] no schedule '{id.EscapeMarkup()}'.");
                return 1;
            }
        )
);
schedulesCommand.Subcommands.Add(removeScheduleCommand);
root.Subcommands.Add(schedulesCommand);

// info ---------------------------------------------------------------------------
var infoCommand = new Command("info", "Show what the daemon offers: supervisors, models, sandbox, tools.");
infoCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                var capabilities = await client.GetCapabilitiesAsync(cancellationToken);
                AnsiConsole.MarkupLine(
                    "[bold]supervisors:[/] "
                        + string.Join(
                            ", ",
                            capabilities.Supervisors.Select(s =>
                                s == capabilities.DefaultSupervisor
                                    ? $"{s.EscapeMarkup()} [dim](default)[/]"
                                    : s.EscapeMarkup()
                            )
                        )
                );
                AnsiConsole.MarkupLine(
                    "[bold]models:[/] "
                        + $"default [dim]({capabilities.DefaultModel?.EscapeMarkup() ?? "offline script"})[/]"
                        + (
                            capabilities.Models.Count > 0
                                ? ", " + string.Join(", ", capabilities.Models.Select(static m => m.EscapeMarkup()))
                                : ""
                        )
                );
                AnsiConsole.MarkupLine($"[bold]sandbox:[/] {capabilities.Sandbox.EscapeMarkup()}");
                AnsiConsole.MarkupLine(
                    $"[bold]memory:[/] {(capabilities.Memory ? "on" : "off")}"
                        + $"   [bold]corpus retrieval:[/] {(capabilities.CorpusRetrieval ? "on" : "off")}"
                );
                AnsiConsole.MarkupLine(
                    "[bold]tools:[/] " + string.Join(", ", capabilities.Tools.Select(static t => t.EscapeMarkup()))
                );
                if (capabilities.CatalogTools.Count > 0)
                {
                    AnsiConsole.MarkupLine(
                        "[bold]catalogue:[/] "
                            + string.Join(", ", capabilities.CatalogTools.Select(static t => t.EscapeMarkup()))
                    );
                }

                return 0;
            }
        )
);
root.Subcommands.Add(infoCommand);

// dash ---------------------------------------------------------------------------
var dashCommand = new Command("dash", "Live dashboard: runs, pending approvals, and the event feed.");
dashCommand.SetAction(
    (parseResult, cancellationToken) => WithClient(parseResult, client => Dashboard.RunAsync(client, cancellationToken))
);
root.Subcommands.Add(dashCommand);

// artifacts ----------------------------------------------------------------------
var artifactNameArgument = new Argument<string?>("name")
{
    Description = "Artifact to download; omitted lists them.",
    Arity = ArgumentArity.ZeroOrOne,
};
var artifactOutputOption = new Option<string?>("--output", "-o")
{
    Description = "Write the artifact here instead of standard output.",
};
var artifactsCommand = new Command("artifacts", "List promoted artifacts, or download one.");
artifactsCommand.Arguments.Add(artifactNameArgument);
artifactsCommand.Options.Add(artifactOutputOption);
artifactsCommand.SetAction(
    (parseResult, cancellationToken) =>
        WithClient(
            parseResult,
            async client =>
            {
                if (parseResult.GetValue(artifactNameArgument) is { Length: > 0 } artifactName)
                {
                    var content = await client.OpenArtifactAsync(artifactName, cancellationToken);
                    if (content is null)
                    {
                        AnsiConsole.MarkupLine($"[red]error:[/] no artifact named '{artifactName.EscapeMarkup()}'.");
                        return 1;
                    }

                    await using (content)
                    {
                        if (parseResult.GetValue(artifactOutputOption) is { Length: > 0 } outputPath)
                        {
                            var file = File.Create(outputPath);
                            await using (file)
                            {
                                await content.CopyToAsync(file, cancellationToken);
                            }

                            Console.WriteLine(outputPath);
                        }
                        else
                        {
                            await content.CopyToAsync(Console.OpenStandardOutput(), cancellationToken);
                        }
                    }

                    return 0;
                }

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
    using var client = new OrkisClient(parseResult.GetValue(socketOption), parseResult.GetValue(tokenOption));
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
                + $"([dim]{OrkisEndpoint.Resolve(parseResult.GetValue(socketOption)).EscapeMarkup()}[/]): "
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
/// The interactive chat loop: start (or rejoin) a conversational run, stream its
/// events, print each assistant reply, and prompt for the next message. The SSE
/// connection survives turn boundaries, so one stream carries the whole session.
/// </summary>
static async Task<int> ChatAsync(
    OrkisClient client,
    string? existingRunId,
    string? firstMessage,
    StartRunRequest template,
    CancellationToken cancellationToken
)
{
    if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
    {
        AnsiConsole.MarkupLine("[red]error:[/] chat needs an interactive terminal.");
        return 1;
    }

    string runId;
    var afterSequence = -1L;
    if (existingRunId is not null)
    {
        runId = existingRunId;
        var transcript = await client.GetTranscriptAsync(runId, cancellationToken);
        if (transcript is null)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] no run '{runId.EscapeMarkup()}'.");
            return 1;
        }

        foreach (var message in transcript.Where(static m => m.Role != "system"))
        {
            RenderChatMessage(message.Role, message.Text);
        }

        // Skip the replay; the transcript above already told the story.
        await foreach (var runEvent in client.StreamEventsAsync(runId, cancellationToken: cancellationToken))
        {
            afterSequence = runEvent.Sequence;
        }

        var next = firstMessage ?? PromptChatInput();
        if (string.IsNullOrWhiteSpace(next))
        {
            return 0;
        }

        RenderChatMessage("user", next);
        await client.ContinueRunAsync(runId, next, cancellationToken);
    }
    else
    {
        var first = firstMessage ?? PromptChatInput();
        if (string.IsNullOrWhiteSpace(first))
        {
            return 0;
        }

        var accepted = await client.StartRunAsync(template with { Prompt = first }, cancellationToken);
        runId = accepted.RunId;
        AnsiConsole.MarkupLine(
            $"[dim]chat {runId.EscapeMarkup()} — empty message leaves; rejoin with: orkis chat --run {runId.EscapeMarkup()}[/]"
        );
        RenderChatMessage("user", first);
    }

    await foreach (var runEvent in client.StreamEventsAsync(runId, afterSequence, follow: true, cancellationToken))
    {
        switch (runEvent)
        {
            case ToolCallProposedEvent or SupervisionDecidedEvent or ToolCallCompletedEvent:
                EventRenderer.Render(runEvent);
                break;
            case RunPausedEvent:
                EventRenderer.Render(runEvent);
                if (!await PromptDecisionsAsync(client, runId, cancellationToken))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]left pending[/] — decide, resume, then rejoin with "
                            + $"[bold]orkis chat --run {runId.EscapeMarkup()}[/]"
                    );
                    return 2;
                }

                break;
            case TurnCompletedEvent turn:
                RenderChatMessage("assistant", turn.FinalTextPreview ?? "");
                var message = PromptChatInput();
                if (string.IsNullOrWhiteSpace(message))
                {
                    AnsiConsole.MarkupLine($"[dim]left chat — rejoin with: orkis chat --run {runId.EscapeMarkup()}[/]");
                    return 0;
                }

                RenderChatMessage("user", message);
                await client.ContinueRunAsync(runId, message, cancellationToken);
                break;
            case RunCompletedEvent completed:
                EventRenderer.Render(completed);
                return completed.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ? 0 : 2;
        }
    }

    return 0;
}

static void RenderChatMessage(string role, string text)
{
    var label = role == "user" ? "[bold cyan]you[/]" : $"[bold magenta]{role.EscapeMarkup()}[/]";
    AnsiConsole.MarkupLine($"\n{label}: {text.EscapeMarkup()}");
}

static string? PromptChatInput()
{
    AnsiConsole.Markup("\n[bold cyan]you[/]: ");
    return Console.ReadLine();
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
