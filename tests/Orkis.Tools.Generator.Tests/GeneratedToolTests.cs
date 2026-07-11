using System.Text.Json;
using Orkis.Tools;

namespace Orkis.Tools.Generator.Tests;

public sealed class GeneratedToolTests
{
    private static ToolCall Call(string argumentsJson, string id = "call-1") =>
        new()
        {
            Id = id,
            ToolName = "any",
            Arguments = JsonDocument.Parse(argumentsJson).RootElement,
        };

    [Fact]
    public void DescriptorCarriesNameRiskAndSchema()
    {
        var tool = new SampleTools().CreateAddTool();

        Assert.Equal("add", tool.Descriptor.Name);
        Assert.Equal("Adds two integers.", tool.Descriptor.Description);
        Assert.Equal(ToolRisk.ReadOnly, tool.Descriptor.Risk);

        var schema = tool.Descriptor.ParametersSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("integer", schema.GetProperty("properties").GetProperty("a").GetProperty("type").GetString());
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["a", "b"], required);
    }

    [Fact]
    public async Task BindsArgumentsInvokesThroughOwnerAndSerializesNonStringResult()
    {
        var owner = new SampleTools();
        var tool = owner.CreateAddTool();

        var result = await tool.InvokeAsync(Call("""{"a":19,"b":23}""", id: "call-42"));

        Assert.Equal("call-42", result.ToolCallId);
        Assert.Equal("42", result.Content);
        Assert.False(result.IsError);
        Assert.Equal(["Add(19, 23)"], owner.InvocationLog);
    }

    [Fact]
    public async Task StaticToolUsesExplicitNameOptionalDefaultsAndParameterDescriptions()
    {
        var tool = SampleTools.CreateGreetTool();

        Assert.Equal("greet", tool.Descriptor.Name);
        Assert.Equal(ToolRisk.Mutating, tool.Descriptor.Risk);

        var nameSchema = tool.Descriptor.ParametersSchema.GetProperty("properties").GetProperty("name");
        Assert.Equal("Name of the person to greet.", nameSchema.GetProperty("description").GetString());

        // 'greeting' is optional: not in required, default applies when omitted.
        var required = tool
            .Descriptor.ParametersSchema.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(["name"], required);

        var withDefault = await tool.InvokeAsync(Call("""{"name":"Ada"}"""));
        Assert.Equal("Hello, Ada!", withDefault.Content);

        var explicitGreeting = await tool.InvokeAsync(Call("""{"name":"Ada","greeting":"Hi"}"""));
        Assert.Equal("Hi, Ada!", explicitGreeting.Content);
    }

    [Fact]
    public async Task MissingRequiredArgumentThrowsArgumentException()
    {
        var tool = SampleTools.CreateGreetTool();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => tool.InvokeAsync(Call("{}")));

        Assert.Contains("name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncMethodProducesAwaitedResultAndTrimsAsyncFromName()
    {
        var tool = new SampleTools().CreateEchoTool();

        Assert.Equal("echo", tool.Descriptor.Name);
        var result = await tool.InvokeAsync(Call("""{"text":"ping"}"""));
        Assert.Equal("ping", result.Content);
    }

    [Fact]
    public async Task EnumParameterParsesCaseInsensitivelyAndListsValuesInSchema()
    {
        var tool = SampleTools.CreateDescribeUnitTool();

        var enumValues = tool
            .Descriptor.ParametersSchema.GetProperty("properties")
            .GetProperty("unit")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(["Celsius", "Fahrenheit"], enumValues);

        var result = await tool.InvokeAsync(Call("""{"unit":"fahrenheit"}"""));
        Assert.Equal("Fahrenheit", result.Content);
    }

    [Fact]
    public async Task ComplexParameterDeserializesFromJson()
    {
        var tool = SampleTools.CreateDescribePointTool();

        var result = await tool.InvokeAsync(Call("""{"point":{"x":3,"y":4}}"""));

        Assert.Equal("(3, 4)", result.Content);
    }

    [Fact]
    public void AggregateFactoryCreatesAllTools()
    {
        var tools = new SampleTools().CreateOrkisTools();

        Assert.Equal(5, tools.Count);
        Assert.Equal(
            ["add", "greet", "echo", "describe_unit", "describe_point"],
            tools.Select(t => t.Descriptor.Name).ToList()
        );
    }
}
