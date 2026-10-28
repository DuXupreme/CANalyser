using System.Collections.ObjectModel;
using CanAnalyzer.Core.Analysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class JoystickAnalyticsViewModel
{
    [ObservableProperty] private double _joystickXMinimum = -1;
    [ObservableProperty] private double _joystickXNeutral;
    [ObservableProperty] private double _joystickXMaximum = 1;
    [ObservableProperty] private double _joystickYMinimum = -1;
    [ObservableProperty] private double _joystickYNeutral;
    [ObservableProperty] private double _joystickYMaximum = 1;
    [ObservableProperty] private string _joystickCorrectionCountText = "—";
    [ObservableProperty] private string _joystickCorrectionDurationText = "Bekende joystickgrenzen nodig";
    [ObservableProperty] private string _joystickFullHoldCountText = "—";
    [ObservableProperty] private string _joystickFullHoldDurationText = "Bekende joystickgrenzen nodig";
    [ObservableProperty] private string _joystickMeasuredTimeText = "—";
    [ObservableProperty] private string _joystickMissingTimeText = "Bereken de analyse";
    [ObservableProperty] private string _joystickInputDefinition = "Kies de X- en Y-signalen en vul hun bekende grenzen in om kleine bediening en voluit te onderscheiden.";
    [ObservableProperty] private string _joystickEventListNote = "Selecteer een bediening om de originele X- en Y-metingen te bekijken.";
    [ObservableProperty] private string _joystickInputPreviewNote = string.Empty;
    [ObservableProperty] private JoystickInputRow? _selectedJoystickInput;
    [ObservableProperty] private PlotModel _joystickInputPreviewModel = EmptyPlot("Selecteer een bediening");
    private JoystickUsageResult? _joystickUsageSummary;

    public ObservableCollection<JoystickAmplitudeCard> JoystickAmplitudeCards { get; } = [];
    public ObservableCollection<JoystickInputRow> JoystickInputRows { get; } = [];
    public bool JoystickSetupRequired => !UseJoystickCalibration
        || !HasValidJoystickAxisCalibration
        || string.IsNullOrWhiteSpace(SelectedJoystickXSignal) || string.IsNullOrWhiteSpace(SelectedJoystickYSignal);
    private bool HasValidJoystickAxisCalibration => IsValidAxis(JoystickXMinimum, JoystickXNeutral, JoystickXMaximum)
        && IsValidAxis(JoystickYMinimum, JoystickYNeutral, JoystickYMaximum);
    private static bool IsValidAxis(double minimum, double neutral, double maximum) =>
        double.IsFinite(minimum) && double.IsFinite(neutral) && double.IsFinite(maximum) && minimum < neutral && neutral < maximum;
    public string JoystickSetupNote => string.IsNullOrWhiteSpace(SelectedJoystickXSignal) || string.IsNullOrWhiteSpace(SelectedJoystickYSignal)
        ? "Kies de twee assen van dezelfde joystick. Ontbrekende of onduidelijke signalen worden niet geraden."
        : !UseJoystickCalibration
            ? "Signalen gekozen. Vul de bekende minimum-, neutraal- en maximumwaarde per as in. Alleen de uitersten in deze log bewijzen niet wat voluit is."
            : !HasValidJoystickAxisCalibration
                ? "Controleer de joystickgrenzen: per as moet minimum < neutraal < maximum zijn."
                : "Kalibratie gebruikt de ingevulde waarden per as. Controleer ze bij een andere joystick of decoder.";

    partial void OnJoystickXMinimumChanged(double value) => InvalidateUsage();
    partial void OnJoystickXNeutralChanged(double value) => InvalidateUsage();
    partial void OnJoystickXMaximumChanged(double value) => InvalidateUsage();
    partial void OnJoystickYMinimumChanged(double value) => InvalidateUsage();
    partial void OnJoystickYNeutralChanged(double value) => InvalidateUsage();
    partial void OnJoystickYMaximumChanged(double value) => InvalidateUsage();
    partial void OnSelectedJoystickInputChanged(JoystickInputRow? value) => BuildJoystickInputPreview(value);

    [RelayCommand]
    private void FillPercentJoystickCalibration() => FillJoystickCalibration(-100, 100);

    [RelayCommand]
    private void FillNormalizedJoystickCalibration() => FillJoystickCalibration(-1, 1);

    private void FillJoystickCalibration(double minimum, double maximum)
    {
        // A convenient preset is not evidence that these are this device's physical limits.
        UseJoystickCalibration = false;
        JoystickXMinimum = JoystickYMinimum = minimum;
        JoystickXNeutral = JoystickYNeutral = 0;
        JoystickXMaximum = JoystickYMaximum = maximum;
        InvalidateUsage();
    }

    private JoystickAxisCalibration? CreateJoystickXCalibration() => UseJoystickCalibration
        ? new(JoystickXMinimum, JoystickXNeutral, JoystickXMaximum) : null;
    private JoystickAxisCalibration? CreateJoystickYCalibration() => UseJoystickCalibration
        ? new(JoystickYMinimum, JoystickYNeutral, JoystickYMaximum) : null;

    private void ClearJoystickUsageSummary()
    {
        _joystickUsageSummary = null;
        JoystickAmplitudeCards.Clear();
        JoystickInputRows.Clear();
        SelectedJoystickInput = null;
        JoystickInputPreviewModel = EmptyPlot("Bereken de joystickanalyse");
        JoystickInputPreviewNote = string.Empty;
        JoystickCorrectionCountText = JoystickFullHoldCountText = JoystickMeasuredTimeText = "—";
        JoystickCorrectionDurationText = JoystickFullHoldDurationText = "Bereken de analyse";
        JoystickMissingTimeText = "Alleen gezamenlijk gemeten X en Y tellen mee";
        JoystickInputDefinition = "Uitslag is de grootste uitslag van X of Y, ten opzichte van de bekende grenzen. Slagkalibratie van de actuatoren is hiervoor niet nodig.";
        JoystickEventListNote = "Bereken de analyse om bedieningen te inspecteren.";
        OnPropertyChanged(nameof(JoystickSetupRequired));
        OnPropertyChanged(nameof(JoystickSetupNote));
    }

    private void UpdateJoystickUsageSummary(JoystickUsageResult result)
    {
        ClearJoystickUsageSummary();
        _joystickUsageSummary = result;
        JoystickMeasuredTimeText = $"{result.CoveredSeconds:N1} s";
        JoystickMissingTimeText = $"{Math.Max(0, result.WindowSeconds - result.CoveredSeconds):N1} s zonder gezamenlijke dekking overgeslagen";
        if (!result.IsCalibrated)
        {
            JoystickCorrectionCountText = JoystickFullHoldCountText = "Kalibratie nodig";
            JoystickCorrectionDurationText = JoystickFullHoldDurationText = "Vul de bekende joystickgrenzen hierboven in";
            JoystickInputDefinition = "De grafieken hieronder tonen alleen de relatieve verdeling binnen deze log. Zonder bekende grenzen zijn neutraal, kleine correcties en voluit niet betrouwbaar vast te stellen.";
            JoystickInputPreviewModel = EmptyPlot("Bekende joystickgrenzen nodig");
            return;
        }

        var colors = new[] { "#64748B", "#0D9488", "#2563EB", "#D97706", "#B45309" };
        for (var i = 0; i < result.AmplitudeBands.Count; i++)
        {
            var band = result.AmplitudeBands[i];
            var range = i == 0 ? $"≤ {100 * band.Upper:N0}%"
                : i == 4 ? $"≥ {100 * band.Lower:N0}% op minstens één as"
                : i == 3 ? $"> {100 * band.Lower:N0}% en < {100 * band.Upper:N0}%"
                : $"> {100 * band.Lower:N0}% t/m {100 * band.Upper:N0}%";
            JoystickAmplitudeCards.Add(new(band.Name, $"{band.Percent:N1}%", $"{band.Seconds:N1} s", range, band.Percent, colors[i]));
        }
        var corrections = result.InputEpisodes.Where(e => e.IsShortCorrection).ToArray();
        JoystickCorrectionCountText = corrections.Length.ToString("N0");
        JoystickCorrectionDurationText = corrections.Length > 0
            ? $"Gem. {corrections.Average(e => e.DurationSeconds):N2} s · {60 * corrections.Length / result.CoveredSeconds:N1} per gemeten minuut"
            : "Geen volledig waargenomen korte kleine correcties";
        JoystickFullHoldCountText = result.FullHolds.Count.ToString("N0");
        JoystickFullHoldDurationText = result.FullHolds.Count > 0
            ? $"Samen {result.FullHolds.Sum(e => e.DurationSeconds):N1} s · langste {result.FullHolds.Max(e => e.DurationSeconds):N1} s"
            : "Geen aaneengesloten voluitperiode van ≥ 2 s";
        var small = result.AmplitudeBands[1].Upper;
        var incomplete = result.InputEpisodes.Count(e => !e.IsComplete);
        JoystickInputDefinition = $"Tijdverdeling over {result.CoveredSeconds:N1} s gemeten tijd in het gekozen venster, inclusief stilstand. "
            + $"Korte kleine correctie = vanuit neutraal (≤ {100 * DeadzoneThreshold:N0}%), maximaal {100 * small:N0}% uitslag, binnen 1 s terug naar neutraal. "
            + $"Voluit vasthouden = minstens 2 s onafgebroken ≥ {100 * SaturationThreshold:N0}% op één of beide assen. "
            + $"{incomplete:N0} bedieningen zonder volledig waargenomen begin/einde tellen niet als correctie. "
            + "Kleine uitslag alleen is dus geen correctie; dit beschrijft het ingangssignaal, niet de bedoeling van de bestuurder.";
        if (result.OutsideCalibrationSeconds > 0)
            JoystickInputDefinition += $" Controleer de kalibratie: {result.OutsideCalibrationSeconds:N1} s ligt buiten de ingevulde grenzen; deze tijd is niet afgeknipt.";

        // Keep the UI bounded; full event arrays remain in the export, without affecting statistics.
        // A bounded heap avoids sorting/materializing every event on the UI thread.
        var correctionRows = new PriorityQueue<JoystickInputRow, double>();
        var fullRows = new PriorityQueue<JoystickInputRow, double>();
        var otherRows = new PriorityQueue<JoystickInputRow, double>();
        static void KeepLongest(PriorityQueue<JoystickInputRow, double> queue, JoystickInputRow row, int limit)
        {
            if (queue.Count < limit) queue.Enqueue(row, row.DurationSeconds);
            else if (queue.TryPeek(out _, out var shortest) && row.DurationSeconds > shortest)
                queue.EnqueueDequeue(row, row.DurationSeconds);
        }
        foreach (var e in result.InputEpisodes)
        {
            var row = new JoystickInputRow(e.IsShortCorrection ? "Korte kleine correctie"
                    : e.IsComplete ? "Overige bediening" : "Onvolledig waargenomen",
                e.StartSeconds, e.EndSeconds, e.PeakAmplitude,
                e.IsComplete ? "Van neutraal naar neutraal" : "Begin/einde of meetdekking ontbreekt");
            KeepLongest(e.IsShortCorrection ? correctionRows : otherRows, row, e.IsShortCorrection ? 70 : 60);
        }
        foreach (var e in result.FullHolds)
            KeepLongest(fullRows, new("Voluit vasthouden ≥ 2 s", e.StartSeconds, e.EndSeconds, null,
                "Aaneengesloten gemeten voluit; kan onderdeel zijn van een bediening"), 70);
        var visible = correctionRows.UnorderedItems.Concat(fullRows.UnorderedItems).Concat(otherRows.UnorderedItems)
            .Select(item => item.Element).OrderBy(row => row.StartSeconds).ToArray();
        foreach (var row in visible) JoystickInputRows.Add(row);
        JoystickEventListNote = $"{visible.Length:N0} van {result.InputEpisodes.Count + result.FullHolds.Count:N0} perioden getoond (max. 70 langste correcties, 70 voluitperioden en 60 overige). "
            + "Selecteer een rij; alle perioden staan in de analyse-export. Voluitperioden kunnen overlappen met een bediening en mogen niet bij het aantal bedieningen worden opgeteld.";
        SelectedJoystickInput = JoystickInputRows.FirstOrDefault(r => r.Kind == "Korte kleine correctie")
            ?? JoystickInputRows.FirstOrDefault();
        if (SelectedJoystickInput is null) JoystickInputPreviewModel = EmptyPlot("Geen bediening in dit tijdvenster");
    }

    private object? CaptureJoystickUsageSummary() => _joystickUsageSummary is null ? null : new
    {
        _joystickUsageSummary.IsCalibrated,
        XCalibration = CreateJoystickXCalibration(),
        YCalibration = CreateJoystickYCalibration(),
        AmplitudeDefinition = "max(abs(normalized X), abs(normalized Y)); independent minimum/neutral/maximum per axis",
        Denominator = "jointly measured time in selected window, including stationary time",
        _joystickUsageSummary.CoveredSeconds,
        _joystickUsageSummary.WindowSeconds,
        _joystickUsageSummary.AmplitudeBands,
        _joystickUsageSummary.InputEpisodes,
        _joystickUsageSummary.FullHolds,
        _joystickUsageSummary.OutsideCalibrationSeconds,
        ShortCorrectionDefinition = "Observed neutral -> peak <= small-band upper limit -> neutral within 1 second; gaps and window-truncated episodes excluded",
        FullHoldDefinition = "At least 2 seconds of continuously observed amplitude >= full threshold; gaps split periods",
        NeutralThreshold = DeadzoneThreshold,
        FullThreshold = SaturationThreshold
    };

    private void BuildJoystickInputPreview(JoystickInputRow? row)
    {
        if (row is null || _joystickUsageSummary is null) return;
        var from = Math.Max(0, row.StartSeconds - 0.5);
        var to = Math.Min(row.EndSeconds + 0.5, from + 30);
        var points = _joystickUsageSummary.Points;
        var lower = 0;
        var upper = points.Count;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (points[middle].Time + points[middle].Seconds <= from) lower = middle + 1;
            else upper = middle;
        }
        var first = lower;
        const int maximumPreviewIntervals = 8000;
        var capped = first + maximumPreviewIntervals < points.Count && points[first + maximumPreviewIntervals].Time < to;
        if (capped) to = points[first + maximumPreviewIntervals].Time;
        var model = new PlotModel { Title = $"{row.Kind} vanaf {row.StartSeconds:N2} s", TitleFontSize = 14, IsLegendVisible = true };
        model.Legends.Add(new Legend { LegendPosition = LegendPosition.TopRight });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Tijd in log [s]", Minimum = from, Maximum = to });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Uitslag per as [% van bekende grens]" });
        model.Annotations.Add(new RectangleAnnotation { MinimumX = row.StartSeconds, MaximumX = row.EndSeconds,
            Fill = OxyColor.FromArgb(24, 37, 99, 235), StrokeThickness = 0, Layer = AnnotationLayer.BelowSeries });
        var xs = new LineSeries { Title = "Joystick X", Color = OxyColor.Parse("#2563EB"), StrokeThickness = 1.5 };
        var ys = new LineSeries { Title = "Joystick Y", Color = OxyColor.Parse("#D97706"), StrokeThickness = 1.5 };
        var previousEnd = double.NaN;
        for (var i = first; i < Math.Min(points.Count, first + maximumPreviewIntervals); i++)
        {
            var point = points[i];
            if (point.Time >= to) break;
            if (!double.IsNaN(previousEnd) && Math.Abs(point.Time - previousEnd) > 1e-9)
            {
                xs.Points.Add(DataPoint.Undefined); ys.Points.Add(DataPoint.Undefined);
            }
            var start = Math.Max(from, point.Time);
            var end = Math.Min(to, point.Time + point.Seconds);
            xs.Points.Add(new(start, 100 * point.X)); xs.Points.Add(new(end, 100 * point.X));
            ys.Points.Add(new(start, 100 * point.Y)); ys.Points.Add(new(end, 100 * point.Y));
            previousEnd = point.Time + point.Seconds;
        }
        model.Series.Add(xs); model.Series.Add(ys);
        JoystickInputPreviewNote = row.Note + (capped ? "; voorvertoning begrensd tot 8.000 gemeten intervallen."
            : to < row.EndSeconds ? "; voorvertoning toont de eerste 30 s." : "; gearceerd is de geselecteerde periode.")
            + " Gekalibreerde X/Y-waarden, vastgehouden tot de volgende meting; meetgaten blijven open. Statistieken gebruiken alle meetintervallen.";
        JoystickInputPreviewModel = model;
    }
}

public sealed record JoystickAmplitudeCard(string Name, string Percentage, string Duration, string Range, double BarValue, string Color);

public sealed record JoystickInputRow(string Kind, double StartSeconds, double EndSeconds, double? PeakAmplitude, string Note)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
    public string Start => $"{StartSeconds:N2} s";
    public string Duration => $"{DurationSeconds:N2} s";
    public string Peak => PeakAmplitude.HasValue ? $"{100 * PeakAmplitude.Value:N0}%" : "—";
}
