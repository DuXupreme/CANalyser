using CanAnalyzer.Core.Analysis;

namespace CanAnalyzer.App.Models;

public sealed record StrokeUsageCard(string Name, string? Signal, string Color, StrokeUsageResult? Result)
{
    private static string Number(double? value) => value.HasValue ? $"{value:N2}" : "—";
    private static string Percent(double? value) => value.HasValue ? $"{value:N1}%" : "—";
    public string Used => Percent(Result?.RobustPercent);
    public string TotalUsed => Percent(Result?.UsedPercent);
    public double BarValue => Result?.RobustPercent ?? 0;
    public string LogRange => $"{Number(Result?.LogMinimum)} → {Number(Result?.LogMaximum)}";
    public string LogStroke => Number(Result?.LogMaximum - Result?.LogMinimum);
    public string WindowRange => $"{Number(Result?.Minimum)} → {Number(Result?.Maximum)}";
    public string RobustRange => $"{Number(Result?.P01)} → {Number(Result?.P99)}";
    public string Low => Percent(Result?.LowPercent);
    public string High => Percent(Result?.HighPercent);
    public string Coverage => Result is null ? "Geen positiesignaal geselecteerd" : $"{Result.SampleCount:N0} meetpunten · {Result.CoveredSeconds:N1} s dekking";
    public string Note => Result is null ? "Kies het gemeten positiesignaal bij de instellingen."
        : !Result.LogMinimum.HasValue ? "Geen geldige posities in de log."
        : Result.SampleCount == 0 ? "Geen meetpunten in dit tijdvenster."
        : Result.LogMinimum == Result.LogMaximum ? "Geen beweging in de log; bereik en percentages zijn niet bepaalbaar."
        : Result.CoveredSeconds <= 0 ? "Geen tijddekking; tijdpercentages zijn niet bepaalbaar."
        : "Referentie: behaalde posities in de volledige log.";
}
