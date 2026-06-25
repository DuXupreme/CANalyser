namespace CanAnalyzer.App.Models;

/// <summary>
/// Traffic distribution row for one CAN ID.
/// </summary>
public sealed record CanTopIdRow(
    string FrameIdHex,
    string Count,
    string SharePercent,
    string MeanDlc,
    string FrameType);

/// <summary>
/// Timing/jitter row for one CAN ID.
/// </summary>
public sealed record CanCycleTimingRow(
    string FrameIdHex,
    string Samples,
    string AvgCycleMs,
    string JitterMs,
    string MinCycleMs,
    string MaxCycleMs,
    string NegativeCycles);
