namespace Orkis;

/// <summary>
/// Pairs an item with a relevance score. Higher scores indicate greater relevance;
/// the scale is defined by the component that produced the score.
/// </summary>
public sealed record Scored<T>(T Item, double Score);
