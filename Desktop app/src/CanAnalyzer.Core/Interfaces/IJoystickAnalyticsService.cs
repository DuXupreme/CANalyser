using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Computes signal-level and joystick-pair analytics over decoded signal series.
/// </summary>
public interface IJoystickAnalyticsService
{
    SignalAnalyticsResult AnalyzeSignal(
        SignalSeries series,
        int histogramBins = 40,
        double threshold = 0.0,
        double changeThreshold = 0.01,
        int deltaHistogramBins = 40);

    JoystickPairAnalyticsResult AnalyzePair(
        SignalSeries xSeries,
        SignalSeries ySeries,
        double deadzoneThreshold = 0.10,
        double saturationThreshold = 0.90,
        int radiusHistogramBins = 30,
        int maxPathPoints = 6000);

    IReadOnlyList<SignalRankingRow> RankSignals(
        IReadOnlyDictionary<string, SignalSeries> seriesByLabel,
        double changeThreshold = 0.01,
        int topN = 20);

    JoystickActuatorComparisonResult AnalyzeJoystickActuatorTracking(
        SignalSeries joystickXSeries,
        SignalSeries joystickYSeries,
        SignalSeries actuatorXSeries,
        SignalSeries actuatorYSeries,
        double deadzoneThreshold = 0.10,
        int vectorHistogramBins = 40,
        int maxPathPoints = 6000);

    DelayAnalysisResult AnalyzeDelay(
        SignalSeries commandSeries,
        SignalSeries responseSeries,
        double searchRangeSeconds = 1.5,
        int resamplePoints = 2000,
        double thresholdFraction = 0.2,
        int maxPlotPoints = 5000);

    FirstResponseDelayResult AnalyzeFirstResponseDelay(
        SignalSeries commandSeries,
        SignalSeries responseSeries,
        double searchRangeSeconds = 1.5,
        double responseThresholdFraction = 0.02,
        int histogramBins = 30);

    ButterflyKinematicsResult AnalyzeButterflyKinematics(
        SignalSeries joystickXSeries,
        SignalSeries joystickYSeries,
        SignalSeries joystickYawSeries,
        SignalSeries actuatorLeftSeries,
        SignalSeries actuatorRightSeries,
        SignalSeries actuatorFrontSeries,
        double deadzoneThreshold = 0.10,
        double delaySearchRangeSeconds = 1.5,
        int delayHistogramBins = 24,
        int maxPlotPoints = 6000);
}
