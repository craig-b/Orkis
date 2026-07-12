using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Orkis.Agents;
using Orkis.Runs;

namespace Orkis.Core.Tests;

public sealed class PriceTableCostCalculatorTests
{
    private static PriceTableCostCalculator CreateCalculator(Action<CostOptions> configure)
    {
        var options = new CostOptions();
        configure(options);
        return new PriceTableCostCalculator(Options.Create(options));
    }

    [Fact]
    public void ComputesInputAndOutputCost()
    {
        var calculator = CreateCalculator(o =>
            o.Models["test-model"] = new ModelPrice { InputPerMillionTokens = 3m, OutputPerMillionTokens = 15m }
        );

        var cost = calculator.Calculate(
            new TokenUsage
            {
                InputTokens = 1_000_000,
                OutputTokens = 200_000,
                ModelId = "test-model",
            }
        );

        Assert.Equal(3m + 3m, cost);
    }

    [Fact]
    public void PricesCacheBucketsSeparately()
    {
        var calculator = CreateCalculator(o =>
        {
            var price = new ModelPrice { InputPerMillionTokens = 3m, OutputPerMillionTokens = 15m };
            price.AdditionalPerMillionTokens["cache_read_input_tokens"] = 0.3m;
            o.Models["test-model"] = price;
        });

        var cost = calculator.Calculate(
            new TokenUsage
            {
                InputTokens = 100_000,
                OutputTokens = 0,
                ModelId = "test-model",
                AdditionalCounts = new Dictionary<string, long>
                {
                    ["cache_read_input_tokens"] = 1_000_000,
                    ["unpriced_bucket"] = 500_000,
                },
            }
        );

        // 100k input at 3/M = 0.3, plus 1M cache reads at 0.3/M = 0.3; unpriced buckets cost zero.
        Assert.Equal(0.6m, cost);
    }

    [Fact]
    public void SnapshotModelIdMatchesBaseIdAtWordBoundary()
    {
        var calculator = CreateCalculator(o =>
        {
            o.Models["gpt-5"] = new ModelPrice { InputPerMillionTokens = 100m, OutputPerMillionTokens = 100m };
            o.Models["gpt-5-mini"] = new ModelPrice { InputPerMillionTokens = 1m, OutputPerMillionTokens = 1m };
        });

        var usage = new TokenUsage { InputTokens = 1_000_000, ModelId = "gpt-5-mini-2025-08-07" };

        // Longest boundary-aware prefix wins: gpt-5-mini, not gpt-5.
        Assert.Equal(1m, calculator.Calculate(usage));
    }

    [Fact]
    public void UnknownModelUsesFallbackOrZero()
    {
        var withFallback = CreateCalculator(o =>
            o.Fallback = new ModelPrice { InputPerMillionTokens = 1m, OutputPerMillionTokens = 1m }
        );
        var withoutFallback = CreateCalculator(_ => { });

        var usage = new TokenUsage
        {
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            ModelId = "mystery-model",
        };

        Assert.Equal(2m, withFallback.Calculate(usage));
        Assert.Equal(0m, withoutFallback.Calculate(usage));
    }
}

public sealed class RunCostTests : IDisposable
{
    private readonly FakeChatClient _chatClient = new();
    private readonly FakeTool _tool = new();
    private readonly ScriptedSupervisor _supervisor = new();
    private readonly InMemoryCheckpointStore _checkpointStore = new();

    public void Dispose() => _chatClient.Dispose();

    private AgentRunner CreateRunner(ICostCalculator costCalculator) =>
        new(
            _chatClient,
            [_tool],
            new FakeSupervisorResolver(_supervisor),
            _checkpointStore,
            TimeProvider.System,
            costCalculator
        );

    [Fact]
    public async Task AccumulatesCostAndAdditionalCountsAcrossModelCalls()
    {
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "fake_tool"));
        _chatClient.Enqueue(TestResponses.Text("done"));

        var result = await CreateRunner(new FixedCostCalculator(0.25m))
            .StartAsync(new AgentRunRequest { Prompt = "go" });

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(0.5m, result.Usage.Cost);
    }

    [Fact]
    public async Task MaxCostWithoutPricingFailsFast()
    {
        var runner = CreateRunner(NullCostCalculator.Instance);
        var request = new AgentRunRequest
        {
            Prompt = "go",
            Budget = new RunBudget { MaxCost = 1m },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(request));
    }

    [Fact]
    public async Task CostBudgetStopsRun()
    {
        _chatClient.Enqueue(TestResponses.ToolCall("call-1", "fake_tool"));

        var result = await CreateRunner(new FixedCostCalculator(1m))
            .StartAsync(
                new AgentRunRequest
                {
                    Prompt = "go",
                    Budget = new RunBudget { MaxCost = 0.5m },
                }
            );

        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
        Assert.Equal(0, _tool.Invocations);
        Assert.Equal(1m, result.Usage.Cost);
    }

    [Fact]
    public async Task SurfacesProviderTokenBucketsInUsage()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 5,
                AdditionalCounts = new() { ["cache_read_input_tokens"] = 7 },
            },
        };
        _chatClient.Enqueue(response);

        var result = await CreateRunner(NullCostCalculator.Instance).StartAsync(new AgentRunRequest { Prompt = "go" });

        Assert.Equal(7, result.Usage.AdditionalTokenCounts["cache_read_input_tokens"]);
    }
}
