using CanAnalyzer.Core.Analysis;

namespace CanAnalyzer.App.Models;

public sealed record StrokeUsageCard(string Name, string? Signal, string Color, CalibratedStrokeResult? Result, string ReferenceNote)
{
    private static string Number(double? value) => value.HasValue ? $"{value:N2}" : "—";
    private static string Percent(double? value) => value.HasValue ? $"{value:N1}%" : "—";
    public string Used => Percent(Result?.LowPercent + Result?.HighPercent);
    public double BarValue => Result?.LowPercent + Result?.HighPercent ?? 0;
    public string WindowRange => $"{Number(Result?.MinimumPercent)} → {Number(Result?.MaximumPercent)} %";
    public string RobustRange => $"{Number(Result?.P01Percent)} → {Number(Result?.P99Percent)} %";
    public string Low => Percent(Result?.LowPercent);
    public string High => Percent(Result?.HighPercent);
    public string Outside => Percent(Result?.OutsidePercent);
    public string Visits => Result is null ? "—" : $"{Result.LowVisits} / {Result.HighVisits}";
    public string Longest => Result is null ? "—" : $"{Result.LongestLowSeconds:N1} / {Result.LongestHighSeconds:N1} s";
    public string CommandEdges => $"{Percent(Result?.SetpointLowPercent)} / {Percent(Result?.SetpointHighPercent)}";
    public string BothEdges => $"{Percent(Result?.BothLowPercent)} / {Percent(Result?.BothHighPercent)}";
    public string TrackingError => Result?.MeanAbsoluteErrorPercent is double e ? $"{e:N1} procentpunt" : "—";
    public string Coverage => Result is null ? "Geen grensanalyse beschikbaar" : $"{Result.CoveredSeconds:N1} / {Result.WindowSeconds:N1} s beoordeeld · setpoint: {Result.SetpointCoveredSeconds:N1} s";
    public string Note => ReferenceNote + (Result is { CoveredSeconds: <= 0 } ? " Geen geldige gezamenlijke tijddekking in deze selectie." : "");
}
