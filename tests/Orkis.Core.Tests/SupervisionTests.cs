using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orkis.Agents;
using Orkis.Supervision;
using Orkis.Tools;

namespace Orkis.Core.Tests;

public sealed class ThresholdSupervisorTests
{
    private static ProposedAction ActionWithRisk(ToolRisk risk) =>
        new()
        {
            RunId = "run-1",
            Call = new ToolCall
            {
                Id = "call-1",
                ToolName = "tool",
                Arguments = JsonDocument.Parse("{}").RootElement,
            },
            Tool = new ToolDescriptor
            {
                Name = "tool",
                Description = "A tool.",
                ParametersSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
                Risk = risk,
            },
        };

    [Fact]
    public async Task AutoApprovesAtOrBelowThresholdWithoutConsultingEscalation()
    {
        var escalation = new ScriptedSupervisor();
        var supervisor = new ThresholdSupervisor(ToolRisk.ReadOnly, escalation);

        var decision = await supervisor.ReviewAsync(ActionWithRisk(ToolRisk.ReadOnly));

        Assert.Equal(SupervisionVerdict.Approved, decision.Verdict);
        Assert.Empty(escalation.Reviewed);
    }

    [Fact]
    public async Task EscalatesAboveThreshold()
    {
        var escalation = new ScriptedSupervisor();
        escalation.Enqueue(SupervisionDecision.Deny("needs a human"));
        var supervisor = new ThresholdSupervisor(ToolRisk.ReadOnly, escalation);

        var decision = await supervisor.ReviewAsync(ActionWithRisk(ToolRisk.Destructive));

        Assert.Equal(SupervisionVerdict.Denied, decision.Verdict);
        Assert.Single(escalation.Reviewed);
    }
}

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void AddOrkisResolvesRunnerWithKeyedSupervisors()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient());
        services.AddOrkis();
        services.AddOrkisSupervisor<AutoApproveSupervisor>();
        services.AddOrkisSupervisor(
            "standard",
            provider => new ThresholdSupervisor(ToolRisk.ReadOnly, new AutoApproveSupervisor())
        );

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<AgentRunner>());
        var resolver = provider.GetRequiredService<ISupervisorResolver>();
        Assert.IsType<AutoApproveSupervisor>(resolver.Resolve(SupervisorKeys.Default));
        Assert.IsType<ThresholdSupervisor>(resolver.Resolve("standard"));
    }
}
