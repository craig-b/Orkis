using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Orkis.Core.Tools;
using Orkis.Diagnostics;
using Orkis.Runs;
using Orkis.Sandboxing;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Agents;

/// <summary>
/// The agent loop: sends the conversation to the model, submits requested tool calls
/// to the supervisor, executes approved ones, and feeds results back — checkpointing
/// after every step so the run can pause and resume at any point.
/// </summary>
public sealed class AgentRunner
{
    private static readonly JsonSerializerOptions StateJsonOptions = CreateStateJsonOptions();

    private readonly IChatClient _chatClient;
    private readonly ISupervisorResolver _supervisorResolver;
    private readonly ICheckpointStore _checkpointStore;
    private readonly TimeProvider _timeProvider;
    private readonly FrozenDictionary<string, ITool> _tools;
    private readonly ChatOptions? _chatOptions;

    public AgentRunner(
        IChatClient chatClient,
        IEnumerable<ITool> tools,
        ISupervisorResolver supervisorResolver,
        ICheckpointStore checkpointStore,
        TimeProvider timeProvider
    )
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(supervisorResolver);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _chatClient = chatClient;
        _supervisorResolver = supervisorResolver;
        _checkpointStore = checkpointStore;
        _timeProvider = timeProvider;
        _tools = tools.ToFrozenDictionary(t => t.Descriptor.Name);
        _chatOptions =
            _tools.Count == 0
                ? null
                : new ChatOptions
                {
                    Tools = [.. _tools.Values.Select(AITool (t) => new ToolDeclaration(t.Descriptor))],
                };
    }

    /// <summary>Starts a new run and executes it until it completes or pauses.</summary>
    public Task<AgentRunResult> StartAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var state = new AgentRunState
        {
            RunId = request.RunId,
            Budget = request.Budget,
            SupervisorKey = request.SupervisorKey,
        };
        if (request.SystemPrompt is not null)
        {
            state.Messages.Add(new ChatMessage(ChatRole.System, request.SystemPrompt));
        }

        state.Messages.Add(new ChatMessage(ChatRole.User, request.Prompt));
        return RunLoopAsync(state, cancellationToken);
    }

    /// <summary>Resumes a paused run from its latest checkpoint. Pending tool calls are re-reviewed.</summary>
    public async Task<AgentRunResult> ResumeAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var checkpoint =
            await _checkpointStore.LoadLatestAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No checkpoint found for run '{runId}'.");

        var state =
            checkpoint.State.Deserialize<AgentRunState>(StateJsonOptions)
            ?? throw new InvalidOperationException($"Checkpoint for run '{runId}' could not be deserialized.");

        if (state.Status is not (RunStatus.Running or RunStatus.AwaitingSupervision))
        {
            throw new InvalidOperationException($"Run '{runId}' has already ended with status {state.Status}.");
        }

        state.Status = RunStatus.Running;
        return await RunLoopAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentRunResult> RunLoopAsync(AgentRunState state, CancellationToken cancellationToken)
    {
        var segmentStart = _timeProvider.GetTimestamp();

        using var activity = OrkisInstrumentation.ActivitySource.StartActivity(OrkisTelemetry.RunActivityName);
        activity?.SetTag("orkis.run.id", state.RunId);
        activity?.SetTag("orkis.supervisor.key", state.SupervisorKey);

        try
        {
            var result = await RunSegmentAsync(state, segmentStart, cancellationToken).ConfigureAwait(false);

            activity?.SetTag("orkis.run.status", result.Status.ToString());
            activity?.SetTag("gen_ai.usage.input_tokens", result.Usage.InputTokens);
            activity?.SetTag("gen_ai.usage.output_tokens", result.Usage.OutputTokens);
            OrkisInstrumentation.SegmentDuration.Record(
                _timeProvider.GetElapsedTime(segmentStart).TotalSeconds,
                new TagList { { "orkis.run.status", result.Status.ToString() } }
            );
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            throw;
        }
    }

    private async Task<AgentRunResult> RunSegmentAsync(
        AgentRunState state,
        long segmentStart,
        CancellationToken cancellationToken
    )
    {
        var supervisor = _supervisorResolver.Resolve(state.SupervisorKey);

        while (true)
        {
            // Resolve pending tool calls first; on resume this is where the run re-enters.
            while (state.PendingToolCalls.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsBudgetExceeded(state, segmentStart, aboutToExecuteTool: true))
                {
                    return await EndSegmentAsync(state, RunStatus.BudgetExceeded, segmentStart, cancellationToken)
                        .ConfigureAwait(false);
                }

                var toolCall = state.PendingToolCalls[0];
                var result = await ResolveToolCallAsync(state, supervisor, toolCall, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    // Supervision is pending: pause with the call still queued.
                    return await EndSegmentAsync(state, RunStatus.AwaitingSupervision, segmentStart, cancellationToken)
                        .ConfigureAwait(false);
                }

                state.Messages.Add(
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent(result.ToolCallId, result.Content)])
                );
                state.PendingToolCalls.RemoveAt(0);
                await CheckpointAsync(state, cancellationToken).ConfigureAwait(false);
            }

            if (IsBudgetExceeded(state, segmentStart, aboutToExecuteTool: false))
            {
                return await EndSegmentAsync(state, RunStatus.BudgetExceeded, segmentStart, cancellationToken)
                    .ConfigureAwait(false);
            }

            var response = await _chatClient
                .GetResponseAsync(state.Messages, _chatOptions, cancellationToken)
                .ConfigureAwait(false);

            foreach (var message in response.Messages)
            {
                state.Messages.Add(message);
            }

            if (response.Usage is { } usage)
            {
                var inputTokens = usage.InputTokenCount ?? 0;
                var outputTokens = usage.OutputTokenCount ?? 0;
                state.InputTokens += inputTokens;
                state.OutputTokens += outputTokens;

                if (inputTokens > 0)
                {
                    OrkisInstrumentation.Tokens.Add(inputTokens, new TagList { { "orkis.token.direction", "input" } });
                }

                if (outputTokens > 0)
                {
                    OrkisInstrumentation.Tokens.Add(
                        outputTokens,
                        new TagList { { "orkis.token.direction", "output" } }
                    );
                }
            }

            var toolCalls = response
                .Messages.SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Select(c => new ToolCall
                {
                    Id = c.CallId,
                    ToolName = c.Name,
                    Arguments = SerializeArguments(c.Arguments),
                })
                .ToList();

            if (toolCalls.Count == 0)
            {
                return await EndSegmentAsync(state, RunStatus.Completed, segmentStart, cancellationToken)
                    .ConfigureAwait(false);
            }

            state.PendingToolCalls.AddRange(toolCalls);
            await CheckpointAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reviews and executes a single tool call, returning its result — or
    /// <see langword="null"/> when supervision is pending and the run must pause.
    /// </summary>
    private async Task<ToolResult?> ResolveToolCallAsync(
        AgentRunState state,
        ISupervisor supervisor,
        ToolCall toolCall,
        CancellationToken cancellationToken
    )
    {
        if (!_tools.TryGetValue(toolCall.ToolName, out var tool))
        {
            RecordToolCall(toolCall.ToolName, "unknown_tool");
            return Error(toolCall, $"Unknown tool '{toolCall.ToolName}'.");
        }

        var decision = await ReviewAsync(state, supervisor, toolCall, tool, cancellationToken).ConfigureAwait(false);

        if (decision.Verdict == SupervisionVerdict.Pending)
        {
            return null;
        }

        if (decision.Verdict == SupervisionVerdict.Denied)
        {
            RecordToolCall(toolCall.ToolName, "denied");
            return Error(toolCall, $"Tool call denied by supervisor: {decision.Reason ?? "no reason given"}");
        }

        if (decision.RequiredSandboxLevel is { } level && tool is not ISandboxedTool)
        {
            RecordToolCall(toolCall.ToolName, "sandbox_rejected");
            return Error(
                toolCall,
                $"Supervisor requires sandbox level {level}, but tool '{toolCall.ToolName}' cannot run sandboxed."
            );
        }

        state.ToolCallCount++;

        using var activity = OrkisInstrumentation.ActivitySource.StartActivity(
            $"{OrkisTelemetry.ExecuteToolActivityPrefix} {toolCall.ToolName}"
        );
        activity?.SetTag("gen_ai.tool.name", toolCall.ToolName);
        activity?.SetTag("gen_ai.tool.call.id", toolCall.Id);
        activity?.SetTag("orkis.tool.risk", tool.Descriptor.Risk.ToString());

        var result = await ExecuteAsync(tool, toolCall, decision.RequiredSandboxLevel, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsError)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Content);
        }

        RecordToolCall(toolCall.ToolName, result.IsError ? "error" : "executed");
        return result;
    }

    private static async Task<SupervisionDecision> ReviewAsync(
        AgentRunState state,
        ISupervisor supervisor,
        ToolCall toolCall,
        ITool tool,
        CancellationToken cancellationToken
    )
    {
        using var activity = OrkisInstrumentation.ActivitySource.StartActivity(OrkisTelemetry.SupervisionActivityName);
        activity?.SetTag("gen_ai.tool.name", toolCall.ToolName);
        activity?.SetTag("orkis.supervisor.key", state.SupervisorKey);
        activity?.SetTag("orkis.tool.risk", tool.Descriptor.Risk.ToString());

        var action = new ProposedAction
        {
            RunId = state.RunId,
            Call = toolCall,
            Tool = tool.Descriptor,
        };
        var decision = await supervisor.ReviewAsync(action, cancellationToken).ConfigureAwait(false);

        activity?.SetTag("orkis.supervision.verdict", decision.Verdict.ToString());
        return decision;
    }

    private static void RecordToolCall(string toolName, string outcome) =>
        OrkisInstrumentation.ToolCalls.Add(
            1,
            new TagList { { "gen_ai.tool.name", toolName }, { "orkis.tool.outcome", outcome } }
        );

    private static async Task<ToolResult> ExecuteAsync(
        ITool tool,
        ToolCall toolCall,
        SandboxLevel? requiredLevel,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return requiredLevel is { } level
                ? await ((ISandboxedTool)tool).InvokeAsync(toolCall, level, cancellationToken).ConfigureAwait(false)
                : await tool.InvokeAsync(toolCall, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(toolCall, $"Tool '{toolCall.ToolName}' failed: {ex.Message}");
        }
    }

    private static ToolResult Error(ToolCall toolCall, string message) =>
        new()
        {
            ToolCallId = toolCall.Id,
            Content = message,
            IsError = true,
        };

    private bool IsBudgetExceeded(AgentRunState state, long segmentStart, bool aboutToExecuteTool)
    {
        var budget = state.Budget;
        if (budget.MaxTokens is { } maxTokens && state.InputTokens + state.OutputTokens > maxTokens)
        {
            return true;
        }

        if (aboutToExecuteTool && budget.MaxToolCalls is { } maxToolCalls && state.ToolCallCount >= maxToolCalls)
        {
            return true;
        }

        return budget.MaxDuration is { } maxDuration
            && state.ActiveDuration + _timeProvider.GetElapsedTime(segmentStart) > maxDuration;
    }

    private async Task<AgentRunResult> EndSegmentAsync(
        AgentRunState state,
        RunStatus status,
        long segmentStart,
        CancellationToken cancellationToken
    )
    {
        state.ActiveDuration += _timeProvider.GetElapsedTime(segmentStart);
        state.Status = status;
        await CheckpointAsync(state, cancellationToken).ConfigureAwait(false);

        return new AgentRunResult
        {
            RunId = state.RunId,
            Status = status,
            FinalText = state.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text,
            Usage = new RunUsage
            {
                InputTokens = state.InputTokens,
                OutputTokens = state.OutputTokens,
                ToolCalls = state.ToolCallCount,
                ActiveDuration = state.ActiveDuration,
            },
        };
    }

    private async Task CheckpointAsync(AgentRunState state, CancellationToken cancellationToken)
    {
        var sequence = state.NextSequence++;
        var checkpoint = new RunCheckpoint
        {
            RunId = state.RunId,
            Sequence = sequence,
            State = JsonSerializer.SerializeToElement(state, StateJsonOptions),
            CreatedAt = _timeProvider.GetUtcNow(),
        };
        await _checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement SerializeArguments(IDictionary<string, object?>? arguments) =>
        JsonSerializer.SerializeToElement(
            arguments ?? new Dictionary<string, object?>(),
            AIJsonUtilities.DefaultOptions
        );

    private static JsonSerializerOptions CreateStateJsonOptions()
    {
        // Chat message types serialize via the Microsoft.Extensions.AI resolvers;
        // Orkis's own state types fall back to the reflection resolver.
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        return options;
    }
}
