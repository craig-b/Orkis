using System.Text.Json;
using Microsoft.Extensions.Options;
using Orkis.Sandboxing;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Runs.FileSystem.Tests;

public sealed class FileApprovalInboxTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "orkis-tests",
        Guid.CreateVersion7().ToString("n")
    );

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private FileApprovalInbox CreateQueue() =>
        new(Options.Create(new FileApprovalInboxOptions { RootPath = _rootPath }));

    private static PendingApproval Approval(string runId = "run-1", string callId = "call-1", long ageSeconds = 0) =>
        new()
        {
            RunId = runId,
            Call = new ToolCall
            {
                Id = callId,
                ToolName = "run_shell_command",
                Arguments = JsonSerializer.Deserialize<JsonElement>("""{"command":"echo hi"}"""),
            },
            Tool = new ToolDescriptor
            {
                Name = "run_shell_command",
                Description = "Runs a shell command.",
                ParametersSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}"""),
                Risk = ToolRisk.Destructive,
            },
            RequestedAt = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero).AddSeconds(ageSeconds),
        };

    [Fact]
    public async Task SubmitThenListPendingRoundTripsTheApproval()
    {
        var queue = CreateQueue();

        await queue.SubmitAsync(Approval());
        var pending = Assert.Single(await queue.ListPendingAsync());

        Assert.Equal("run-1", pending.RunId);
        Assert.Equal("call-1", pending.Call.Id);
        Assert.Equal("run_shell_command", pending.Call.ToolName);
        Assert.Equal(ToolRisk.Destructive, pending.Tool.Risk);
        Assert.True(
            JsonElement.DeepEquals(
                JsonSerializer.Deserialize<JsonElement>("""{"command":"echo hi"}"""),
                pending.Call.Arguments
            )
        );
    }

    [Fact]
    public async Task GetDecisionReturnsNullWhilePendingAndForUnknownCalls()
    {
        var queue = CreateQueue();
        await queue.SubmitAsync(Approval());

        Assert.Null(await queue.GetDecisionAsync("run-1", "call-1"));
        Assert.Null(await queue.GetDecisionAsync("run-1", "unknown-call"));
        Assert.Null(await queue.GetDecisionAsync("unknown-run", "call-1"));
    }

    [Fact]
    public async Task DecisionRoundTripsAcrossANewInstance()
    {
        await CreateQueue().SubmitAsync(Approval());
        await CreateQueue().DecideAsync("run-1", "call-1", SupervisionDecision.Approve(SandboxLevel.Standard));

        var decision = await CreateQueue().GetDecisionAsync("run-1", "call-1");

        Assert.NotNull(decision);
        Assert.Equal(SupervisionVerdict.Approved, decision.Verdict);
        Assert.Equal(SandboxLevel.Standard, decision.RequiredSandboxLevel);
        Assert.Empty(await CreateQueue().ListPendingAsync());
    }

    [Fact]
    public async Task DenialReasonRoundTrips()
    {
        var queue = CreateQueue();
        await queue.SubmitAsync(Approval());
        await queue.DecideAsync("run-1", "call-1", SupervisionDecision.Deny("not on my watch"));

        var decision = await queue.GetDecisionAsync("run-1", "call-1");

        Assert.NotNull(decision);
        Assert.Equal(SupervisionVerdict.Denied, decision.Verdict);
        Assert.Equal("not on my watch", decision.Reason);
    }

    [Fact]
    public async Task SubmittingTheSameCallTwiceKeepsOneEntry()
    {
        var queue = CreateQueue();

        await queue.SubmitAsync(Approval());
        await queue.SubmitAsync(Approval(ageSeconds: 60));

        var pending = Assert.Single(await queue.ListPendingAsync());
        Assert.Equal(Approval().RequestedAt, pending.RequestedAt);
    }

    [Fact]
    public async Task PendingApprovalsAreOrderedOldestFirst()
    {
        var queue = CreateQueue();

        await queue.SubmitAsync(Approval(callId: "call-b", ageSeconds: 60));
        await queue.SubmitAsync(Approval(callId: "call-a", ageSeconds: 0));
        await queue.SubmitAsync(Approval(runId: "run-2", callId: "call-c", ageSeconds: 30));

        var pending = await queue.ListPendingAsync();

        Assert.Equal(["call-a", "call-c", "call-b"], pending.Select(p => p.Call.Id));
    }

    [Fact]
    public async Task DecidingAnUnknownApprovalThrows()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateQueue().DecideAsync("run-1", "call-1", SupervisionDecision.Approve())
        );
    }

    [Fact]
    public async Task DecidingTwiceThrows()
    {
        var queue = CreateQueue();
        await queue.SubmitAsync(Approval());
        await queue.DecideAsync("run-1", "call-1", SupervisionDecision.Approve());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.DecideAsync("run-1", "call-1", SupervisionDecision.Deny("changed my mind"))
        );
    }

    [Fact]
    public async Task APendingVerdictCannotBeRecordedAsADecision()
    {
        var queue = CreateQueue();
        await queue.SubmitAsync(Approval());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            queue.DecideAsync("run-1", "call-1", SupervisionDecision.Defer())
        );
    }

    [Fact]
    public async Task HostileRunAndCallIdsStayInsideTheRootDirectory()
    {
        var queue = CreateQueue();

        await queue.SubmitAsync(Approval(runId: "../run-escape", callId: "../../call-escape"));
        await queue.DecideAsync("../run-escape", "../../call-escape", SupervisionDecision.Approve());

        var decision = await queue.GetDecisionAsync("../run-escape", "../../call-escape");
        Assert.NotNull(decision);

        var fullRoot = Path.GetFullPath(_rootPath);
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            Assert.StartsWith(fullRoot + Path.DirectorySeparatorChar, file, StringComparison.Ordinal);
        }
    }
}
