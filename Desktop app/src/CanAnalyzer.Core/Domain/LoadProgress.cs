namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Progress status for long-running operations.
/// </summary>
public sealed record LoadProgress(string Label, int Percent);
