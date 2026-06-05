namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Cached XY arrays for one decoded signal label.
/// </summary>
public sealed record SignalSeries(string Label, float[] Time, float[] Value);
