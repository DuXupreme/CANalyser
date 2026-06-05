namespace CanAnalyzer.App.Models;

/// <summary>
/// Row shown in joystick analytics ranking table.
/// </summary>
public sealed record SignalRankingRowModel(
    string Signal,
    int Samples,
    double DurationSeconds,
    double Range,
    double StandardDeviation,
    int ChangeEvents,
    double ChangesPerSecond,
    double MaximumAbsoluteDelta,
    double MaximumAbsoluteSlope,
    double ActivityScore);
