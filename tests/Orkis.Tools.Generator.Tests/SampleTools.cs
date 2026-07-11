using Orkis.Tools;

namespace Orkis.Tools.Generator.Tests;

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}

public sealed record Point(int X, int Y);

public sealed partial class SampleTools
{
    /// <summary>Proves generated tools invoke through the owning instance.</summary>
    public List<string> InvocationLog { get; } = [];

    [OrkisTool(Description = "Adds two integers.", Risk = ToolRisk.ReadOnly)]
    public int Add(int a, int b)
    {
        InvocationLog.Add($"Add({a}, {b})");
        return a + b;
    }

    [OrkisTool(Name = "greet", Description = "Greets someone.")]
    public static string Greet(
        [OrkisToolParameter("Name of the person to greet.")] string name,
        string greeting = "Hello"
    ) => $"{greeting}, {name}!";

    [OrkisTool(Description = "Echoes text after a delay.", Risk = ToolRisk.ReadOnly)]
    public async Task<string> EchoAsync(string text, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1, cancellationToken);
        InvocationLog.Add($"Echo({text})");
        return text;
    }

    [OrkisTool(Description = "Names a temperature unit.", Risk = ToolRisk.ReadOnly)]
    public static string DescribeUnit(TemperatureUnit unit) => unit.ToString();

    [OrkisTool(Description = "Describes a point.", Risk = ToolRisk.ReadOnly)]
    public static string DescribePoint(Point point) => $"({point.X}, {point.Y})";
}
