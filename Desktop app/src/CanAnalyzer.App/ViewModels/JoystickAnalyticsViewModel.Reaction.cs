using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace CanAnalyzer.App.ViewModels;

public sealed record ReactionEventRow(MotionResponseEvent Measurement)
{
    public string Time => Measurement.CommandTimeSeconds.ToString("0.000", CultureInfo.CurrentCulture);
    public string Kind => Measurement.Outcome == MotionResponseOutcome.NoNewDirectionRequest
        ? "Setpointwijziging zonder bevestigde positievraag" : Measurement.Kind switch
    {
        MotionResponseKind.Start => "Start vanuit rust",
        MotionResponseKind.Reversal => "Omkeren",
        _ => "Correctie tijdens beweging"
    };
    public string Direction => Measurement.Direction switch { > 0 => "Stijgend", < 0 => "Dalend", _ => "Geen nieuwe richting" };
    public bool Accepted => Measurement.Outcome == MotionResponseOutcome.Accepted;
    public string Delay => Measurement.DelaySeconds is { } value ? $"{value * 1000:0.#} ms" : "—";
    public string Bounds => Measurement.DelayLowerBoundSeconds is { } low && Measurement.DelayUpperBoundSeconds is { } high
        ? $"{low * 1000:0.#}–{high * 1000:0.#} ms" : "—";
    public string Outcome => Measurement.Outcome switch
    {
        MotionResponseOutcome.Accepted => "Gemeten",
        MotionResponseOutcome.AlreadyMoving => "Positie bewoog al; nieuwe reactie niet te onderscheiden",
        MotionResponseOutcome.NoPrehistory => "Te weinig voorgeschiedenis voor een betrouwbare start",
        MotionResponseOutcome.NoResponseWithinWindow => "Geen bevestigde reactie binnen de zoektijd",
        MotionResponseOutcome.InterruptedByCommand => "Onderbroken door een volgend commando",
        MotionResponseOutcome.MeasurementGap => "Meetgat in commando of positie",
        MotionResponseOutcome.NoNewDirectionRequest => "Geen bevestigde nieuwe positievraag buiten de marge",
        _ => "Log/venster stopt voordat de reactie bevestigd kon worden"
    };
}

public sealed record ReactionReferenceSummary(string Name, string Explanation, string Mean,
    string Minimum, string Maximum, string P95, string Count);

public sealed partial class JoystickAnalyticsViewModel
{
    private static readonly ConditionalWeakTable<IReadOnlyList<MeasurementGap>, ReactionGapIndex> ReactionGapIndexes = new();
    private MotionResponseResult? _exportMotionResponse;
    private MotionResponseResult? _exportPositionRequest;
    private MotionTrackingResult? _exportMotionTracking;
    private SignalSeries? _reactionCommand;
    private SignalSeries? _reactionFeedback;
    private bool _syncingReactionPair;
    private string? _reactionUnitMismatchNote;
    public IReadOnlyList<string> ReactionActuators { get; } = ["Links", "Rechts", "Voor", "Vrije signaalkeuze"];
    public IReadOnlyList<string> ReactionKinds { get; } = ["Start vanuit rust", "Correcties en omkeren"];
    public IReadOnlyList<string> ReactionReferences { get; } = ["Setpointwijziging → reactie", "Positievraag → reactie"];
    public ObservableCollection<ReactionReferenceSummary> ReactionReferenceSummaries { get; } = [];
    public ObservableCollection<ReactionEventRow> ReactionEvents { get; } = [];
    [ObservableProperty] private string _selectedReactionActuator = "Vrije signaalkeuze";
    [ObservableProperty] private string _selectedReactionKind = "Start vanuit rust";
    [ObservableProperty] private string _selectedReactionReference = "Positievraag → reactie";
    [ObservableProperty] private ReactionEventRow? _selectedReactionEvent;
    [ObservableProperty] private bool _useAutomaticResponseThreshold = true;
    [ObservableProperty] private double _responseThresholdAbsolute = .1;
    [ObservableProperty] private bool _useAutomaticPositionTolerance = true;
    [ObservableProperty] private double _positionToleranceAbsolute = .1;
    [ObservableProperty] private bool _calculateCorrelation;
    [ObservableProperty] private string _reactionMeanText = "—";
    [ObservableProperty] private string _reactionMinimumText = "—";
    [ObservableProperty] private string _reactionMaximumText = "—";
    [ObservableProperty] private string _reactionP95Text = "—";
    [ObservableProperty] private string _reactionCountText = "Geen metingen";
    [ObservableProperty] private string _reactionStatus = "Kies setpoint en gemeten positie en bereken de analyse.";
    [ObservableProperty] private string _reactionThresholdText = "Automatische bewegingsdetectie";
    [ObservableProperty] private string _reactionTimingText = string.Empty;
    [ObservableProperty] private string _reactionDetailText = "Selecteer een stuuractie om de meting te controleren.";
    [ObservableProperty] private string _reactionReferenceExplanation = string.Empty;
    [ObservableProperty] private string _motionTrackingMeanText = "—";
    [ObservableProperty] private string _motionTrackingP95Text = "—";
    [ObservableProperty] private string _motionTrackingMaximumText = "—";
    [ObservableProperty] private string _motionTrackingStatus = "Kies setpoint en positie in dezelfde eenheid en met hetzelfde nulpunt.";
    [ObservableProperty] private PlotModel _reactionEventPlot = EmptyPlot("Selecteer een stuuractie");
    [ObservableProperty] private PlotModel _motionTrackingPlot = EmptyPlot("Selecteer een stuuractie of de grootste afwijking");

    partial void OnUseAutomaticResponseThresholdChanged(bool value) => InvalidateDelay();
    partial void OnResponseThresholdAbsoluteChanged(double value) => InvalidateDelay();
    partial void OnUseAutomaticPositionToleranceChanged(bool value) => InvalidateDelay();
    partial void OnPositionToleranceAbsoluteChanged(double value) => InvalidateDelay();
    partial void OnCalculateCorrelationChanged(bool value) => InvalidateDelay();
    partial void OnSelectedReactionKindChanged(string value) => RefreshReactionSummary();
    partial void OnSelectedReactionReferenceChanged(string value) => RefreshReactionSummary();
    partial void OnSelectedReactionEventChanged(ReactionEventRow? value) => RefreshReactionEvent();
    partial void OnSelectedReactionActuatorChanged(string value)
    {
        if (_loadingDataset || _syncingReactionPair) return;
        SelectReactionPairFromActuator();
        InvalidateDelay();
    }

    private void SelectReactionPairFromActuator()
    {
        var index = SelectedReactionActuator switch { "Links" => 0, "Rechts" => 1, "Voor" => 2, _ => -1 };
        if (index < 0) return;
        _profileSelectionDepth++;
        _syncingReactionPair = true;
        try
        {
            SelectedCommandSignal = ActuatorRanges[index].SetpointSignal;
            SelectedResponseSignal = new[] { SelectedActuatorLeftSignal, SelectedActuatorRightSignal, SelectedActuatorFrontSignal }[index];
        }
        finally { _syncingReactionPair = false; _profileSelectionDepth--; }
        InvalidateDelay();
    }

    private void SelectReactionPairFromOverview(ActuatorAnalysisProfile actuator)
    {
        var name = actuator.Name.ToLowerInvariant();
        var index = name.Contains("left") || name.Contains("links") ? 0 : name.Contains("right") || name.Contains("rechts") ? 1
            : name.Contains("front") || name.Contains("voor") ? 2 : -1;
        if (index < 0)
        {
            var feedback = new[] { SelectedActuatorLeftSignal, SelectedActuatorRightSignal, SelectedActuatorFrontSignal };
            index = Array.FindIndex(feedback, label => Identity(label) == actuator.Feedback && actuator.Feedback is not null);
        }
        // Opening a result is navigation. Do not route the two signal updates through
        // the editing path, which would rewrite the previously selected actuator role.
        var wasSyncing = _syncingReactionPair;
        _profileSelectionDepth++;
        _syncingReactionPair = true;
        try
        {
            SelectedReactionActuator = index < 0 ? "Vrije signaalkeuze" : ReactionActuators[index];
            SelectedCommandSignal = Label(actuator.Command);
            SelectedResponseSignal = Label(actuator.Feedback);
            RefreshPositionReferenceConfirmation();
        }
        finally { _syncingReactionPair = wasSyncing; _profileSelectionDepth--; }
        InvalidateDelay();
    }

    private void RefreshSelectedActuatorFeedback()
    {
        if (!_loadingDataset && !_syncingReactionPair) SelectReactionPairFromActuator();
    }

    private void SyncReactionPairToActuator()
    {
        if (_loadingDataset || _syncingReactionPair) return;
        var index = SelectedReactionActuator switch { "Links" => 0, "Rechts" => 1, "Voor" => 2, _ => -1 };
        if (index < 0) return;
        _syncingReactionPair = true;
        try
        {
            ActuatorRanges[index].SetpointSignal = SelectedCommandSignal;
            if (index == 0) SelectedActuatorLeftSignal = SelectedResponseSignal;
            else if (index == 1) SelectedActuatorRightSignal = SelectedResponseSignal;
            else SelectedActuatorFrontSignal = SelectedResponseSignal;
        }
        finally { _syncingReactionPair = false; }
    }

    private void ClearReactionResults(string? explanation = null)
    {
        SetReactionSourcePreview(null);
        _exportMotionResponse = null; _exportPositionRequest = null; _exportMotionTracking = null;
        _reactionUnitMismatchNote = null;
        _reactionCommand = _reactionFeedback = null;
        RefreshReferenceSummaries();
        ReactionEvents.Clear(); SelectedReactionEvent = null;
        ReactionMeanText = ReactionMinimumText = ReactionMaximumText = ReactionP95Text = "—";
        ReactionCountText = "Geen metingen";
        ReactionStatus = explanation ?? "Instellingen gewijzigd. Klik op Bereken analyse.";
        ReactionThresholdText = "Bewegingsgrens nog niet berekend"; ReactionTimingText = string.Empty;
        ReactionReferenceExplanation = ReferenceExplanation(SelectedReactionReference == ReactionReferences[1]);
        MotionTrackingMeanText = MotionTrackingP95Text = MotionTrackingMaximumText = "—";
        MotionTrackingStatus = "Nog geen berekende volgafwijking voor deze selectie.";
        ReactionEventPlot = EmptyPlot("Selecteer een stuuractie");
        MotionTrackingPlot = EmptyPlot("Bereken analyse");
    }

    private void RefreshReactionSummary()
    {
        RefreshReferenceSummaries();
        ReactionEvents.Clear(); SelectedReactionEvent = null;
        var result = SelectedReferenceResult;
        ReactionReferenceExplanation = ReferenceExplanation(SelectedReactionReference == ReactionReferences[1]);
        if (result is null)
        {
            ReactionMeanText = ReactionMinimumText = ReactionMaximumText = ReactionP95Text = "—";
            ReactionCountText = "Geen geldige metingen";
            ReactionStatus = _reactionUnitMismatchNote ?? "Geen berekende metingen voor deze referentie.";
            RefreshReactionEvent();
            OnPropertyChanged(nameof(ReactionGapSummary));
            return;
        }
        var starts = SelectedReactionKind == ReactionKinds[0];
        foreach (var entry in result.Events.Where(e => (e.Kind == MotionResponseKind.Start) == starts))
            ReactionEvents.Add(new(entry));
        var accepted = ReactionEvents.Where(e => e.Accepted).Select(e => e.Measurement.DelaySeconds!.Value).Order().ToArray();
        static string Ms(double? seconds) => seconds is { } value ? $"{value * 1000:0.#} ms" : "—";
        ReactionMeanText = Ms(accepted.Length > 0 ? accepted.Average() : null);
        ReactionMinimumText = Ms(accepted.Length > 0 ? accepted[0] : null);
        ReactionMaximumText = Ms(accepted.Length > 0 ? accepted[^1] : null);
        ReactionP95Text = Ms(accepted.Length > 0 ? Percentile(accepted, .95) : null);
        ReactionCountText = $"{accepted.Length:N0} gemeten / {ReactionEvents.Count:N0} gevonden · {ReactionEvents.Count - accepted.Length:N0} uitgesloten";
        ReactionStatus = ReactionEvents.Count == 0
            ? "Geen herkenbare stuuracties in deze categorie. Controleer signalen, kies de andere categorie of een ander tijdvenster."
            : accepted.Length == 0
                ? "Geen betrouwbare reactietijd in deze categorie. De tabel toont per stuuractie waarom deze is uitgesloten; dit betekent niet 0 ms."
                : "Cijfers gelden uitsluitend voor de gemeten stuuracties in deze categorie. Selecteer een rij om setpoint, positie en meetgrens te controleren.";
        var withoutRequest = result.Events.Count(e => e.Outcome == MotionResponseOutcome.NoNewDirectionRequest && e.Kind != MotionResponseKind.Start);
        if (starts && result.Reference == MotionResponseReference.PositionRequest && withoutRequest > 0)
            ReactionStatus += $" Daarnaast {withoutRequest:N0} setpointwijzigingen zonder bevestigde nieuwe positievraag; die staan onder Correcties en omkeren.";
        ReactionThresholdText = $"{(result.AutoThreshold ? "Automatisch" : "Handmatig")}: positie moet {result.ResponseThreshold:G4} signaaleenheid veranderen; commandogrens {result.CommandThreshold:G4}.";
        if (result.Reference == MotionResponseReference.PositionRequest)
            ReactionThresholdText += $" Positiemarge: {result.PositionTolerance:G4} signaaleenheid; analysegrens, geen bekende regelband van de actuator.";
        ReactionTimingText = $"Sampleafstand setpoint / positie: {result.CommandSampleIntervalSeconds * 1000:0.#} / {result.ResponseSampleIntervalSeconds * 1000:0.#} ms. " +
            $"Zoektijd maximaal {DelaySearchRangeSeconds:0.###} s. De tijd omvat het bereiken van de meetgrens; een grotere grens of langzamere beweging kan deze verlengen. " +
            "Het meetinterval per rij begrenst de waargenomen grenspassages door samples, niet de fysieke bewegingsstart.";
        var bars = new PlotModel { Title = "Verdeling gemeten reactietijden", TitleFontSize = 14 };
        bars.Axes.Add(Axis(AxisPosition.Bottom, "Reactietijd [ms]", 0));
        bars.Axes.Add(Axis(AxisPosition.Left, "Aantal stuuracties", 0));
        if (accepted.Length > 0)
        {
            var width = Math.Max(result.SamplingResolutionSeconds * 1000, Math.Max(1, accepted[^1] * 1000 / 20));
            var series = new RectangleBarSeries { FillColor = OxyColor.Parse("#2563EB") };
            foreach (var bucket in accepted.GroupBy(x => (int)Math.Floor(x * 1000 / width)))
                series.Items.Add(new RectangleBarItem(bucket.Key * width, 0, (bucket.Key + 1) * width, bucket.Count()));
            bars.Series.Add(series);
        }
        else bars.Title = "Geen gemeten reacties in deze categorie";
        DelayHistogramModel = bars;
        SelectedReactionEvent = ReactionEvents.FirstOrDefault(e => e.Accepted) ?? ReactionEvents.FirstOrDefault();
        if (SelectedReactionEvent is null) RefreshReactionEvent();
        OnPropertyChanged(nameof(ReactionGapSummary));
    }

    private MotionResponseResult? SelectedReferenceResult => SelectedReactionReference == ReactionReferences[1]
        ? _exportPositionRequest : _exportMotionResponse;

    private static string ReferenceExplanation(bool positionRequest) => positionRequest
        ? "Tijd vanaf een setpointupdate die een nieuwe bewegingsrichting vraagt buiten de positiemarge, tot bevestigde beweging in die richting. Setpoint en feedback moeten dezelfde eenheid en nulreferentie hebben."
        : "Tijd vanaf een herkenbare setpointwijziging tot bevestigde positieverandering in de richting van die wijziging. Een dalend setpoint kan nog boven de positie liggen; deze tijd is dan geen zuivere actuatorvertraging.";

    private void RefreshReferenceSummaries()
    {
        ReactionReferenceSummaries.Clear();
        var starts = SelectedReactionKind == ReactionKinds[0];
        for (var i = 0; i < 2; i++)
        {
            var result = i == 0 ? _exportMotionResponse : _exportPositionRequest;
            var candidates = result?.Events.Where(e => (e.Kind == MotionResponseKind.Start) == starts).ToArray() ?? [];
            var accepted = candidates.Where(e => e.Outcome == MotionResponseOutcome.Accepted).Select(e => e.DelaySeconds!.Value).Order().ToArray();
            static string Ms(double? seconds) => seconds is { } value ? $"{value * 1000:0.#} ms" : "—";
            ReactionReferenceSummaries.Add(new(ReactionReferences[i], i == 0
                ? "Vanaf de verandering van het setpoint zelf."
                : _reactionUnitMismatchNote ?? "Vanaf een nieuwe positievraag buiten de marge rond feedback.",
                Ms(accepted.Length > 0 ? accepted.Average() : null),
                Ms(accepted.Length > 0 ? accepted[0] : null),
                Ms(accepted.Length > 0 ? accepted[^1] : null),
                Ms(accepted.Length > 0 ? Percentile(accepted, .95) : null),
                result is null ? "Nog niet berekend" : $"{accepted.Length:N0} gemeten · {candidates.Length - accepted.Length:N0} uitgesloten"));
        }
    }

    [RelayCommand]
    private void InspectReactionReference(string reference) => SelectedReactionReference = reference;

    private void PublishMotionTracking(MotionTrackingResult result)
    {
        _exportMotionTracking = result;
        MotionTrackingMeanText = result.MeanAbsoluteError?.ToString("0.###", CultureInfo.CurrentCulture) ?? "—";
        MotionTrackingP95Text = result.Percentile95AbsoluteError?.ToString("0.###", CultureInfo.CurrentCulture) ?? "—";
        MotionTrackingMaximumText = result.MaximumAbsoluteError?.ToString("0.###", CultureInfo.CurrentCulture) ?? "—";
        MotionTrackingStatus = $"{result.MovingSeconds:N2} s met detecteerbare verandering / {result.CoveredSeconds:N2} s gezamenlijke dekking. " +
            "Afwijking in oorspronkelijke signaaleenheden, tijdgewogen. Beweging: verandering groter dan de meetgrens in de laatste 100 ms. Controleer gelijke eenheid en nulpunt.";
    }

    [RelayCommand]
    private void InspectReactionMetric(string metric)
    {
        var rows = ReactionEvents.Where(e => e.Accepted).OrderBy(e => e.Measurement.DelaySeconds).ToArray();
        if (rows.Length == 0) return;
        var mean = rows.Average(e => e.Measurement.DelaySeconds!.Value);
        SelectedReactionEvent = metric switch
        {
            "min" => rows[0],
            "max" => rows[^1],
            "p95" => rows[(int)Math.Ceiling(.95 * rows.Length) - 1],
            _ => rows.MinBy(e => Math.Abs(e.Measurement.DelaySeconds!.Value - mean))
        };
    }

    [RelayCommand]
    private void ShowWorstTracking()
    {
        if (_exportMotionTracking?.WorstTimeSeconds is not { } time) return;
        MotionTrackingPlot = BuildReactionPlot(time - .5, time + .5, null, true);
    }

    private void RefreshReactionEvent()
    {
        if (SelectedReactionEvent is not { } row || _reactionCommand is null || _reactionFeedback is null)
        {
            ReactionEventPlot = BuildReactionSourcePreview();
            MotionTrackingPlot = EmptyPlot("Selecteer een stuuractie of de grootste afwijking");
            ReactionDetailText = _reactionSourcePreview is null ? "Selecteer een stuuractie om de meting te controleren." :
                "De grafiek toont de onverschoven bronwaarden. Er is geen geselecteerde meetbare stuuractie; dit betekent niet 0 ms. Controleer de signalen en het tijdvenster hierboven.";
            return;
        }
        var e = row.Measurement;
        var start = Math.Min(e.CommandTimeSeconds, e.OriginCommandTimeSeconds ?? e.CommandTimeSeconds) - Math.Max(.15, (SelectedReferenceResult?.SamplingResolutionSeconds ?? .01) * 5);
        var end = Math.Max(e.CommandTimeSeconds + .15, (e.ReactionTimeSeconds ?? e.ResponseSearchEndSeconds) + .15);
        var previewLimited = end - start > 30;
        if (previewLimited) start = end - 30;
        ReactionEventPlot = BuildReactionPlot(start, end, e, false);
        MotionTrackingPlot = BuildReactionPlot(start, end, e, true);
        ReactionDetailText = $"{SelectedReactionReference} · {row.Kind} op {row.Time} s · {row.Outcome}. " +
            (row.Accepted ? $"Reactietijd {row.Delay}; mogelijk interval door sampleafstand {row.Bounds}. " : string.Empty) +
            (e.OriginCommandTimeSeconds is { } origin && origin < e.CommandTimeSeconds
                ? $"De bijbehorende setpointwijziging begon op {origin:0.000} s; de positievraag ontstond {(e.CommandTimeSeconds - origin) * 1000:0.#} ms later. " : string.Empty) +
            (previewLimited ? "De grafiek toont de laatste 30 s van deze actie; eerdere startmomenten staan buiten beeld. De berekening gebruikt alle oorspronkelijke samples. " : string.Empty) +
            "Positie moet de gemarkeerde meetgrens aanhoudend passeren. De lijnen tonen de oorspronkelijke, niet in tijd verschoven meetwaarden.";
    }

    private PlotModel BuildReactionPlot(double start, double end, MotionResponseEvent? measurement, bool error)
    {
        var model = new PlotModel { Title = error ? "Volgafwijking rond geselecteerde actie" : SelectedReactionReference, TitleFontSize = 15, IsLegendVisible = true };
        model.Legends.Add(new Legend { LegendPosition = LegendPosition.TopLeft, LegendPlacement = LegendPlacement.Outside, LegendOrientation = LegendOrientation.Horizontal });
        model.Axes.Add(Axis(AxisPosition.Bottom, "Tijd in log [s]", start, end));
        model.Axes.Add(Axis(AxisPosition.Left, error ? "Setpoint − positie [signaaleenheid]" : "Positie [signaaleenheid]"));
        if (_reactionCommand is not { } command || _reactionFeedback is not { } feedback) return model;
        var gaps = MeasurementGapSelection.ForSeries(_dataset?.ImportReport, command, feedback);
        var gapIndex = ReactionGapIndexes.GetValue(gaps, static intervals => new ReactionGapIndex(intervals));
        if (error)
        {
            var line = new StairStepSeries { Title = "Volgafwijking", Color = OxyColor.Parse("#7C3AED"), StrokeThickness = 1.5 };
            var ci = Math.Max(0, UpperBound(command.Time, start) - 1);
            var ri = Math.Max(0, UpperBound(feedback.Time, start) - 1);
            var cg = 5 * (_exportMotionResponse?.CommandSampleIntervalSeconds ?? 0);
            var rg = 5 * (_exportMotionResponse?.ResponseSampleIntervalSeconds ?? 0);
            var cursor = Math.Max(start, Math.Max(command.Time.FirstOrDefault(), feedback.Time.FirstOrDefault()));
            while (ci + 1 < command.Time.Length && ri + 1 < feedback.Time.Length && cursor <= end)
            {
                while (ci + 1 < command.Time.Length && command.Time[ci + 1] <= cursor) ci++;
                while (ri + 1 < feedback.Time.Length && feedback.Time[ri + 1] <= cursor) ri++;
                if (ci + 1 >= command.Time.Length || ri + 1 >= feedback.Time.Length) break;
                var next = Math.Min(command.Time[ci + 1], feedback.Time[ri + 1]);
                var valid = command.Time[ci + 1] - command.Time[ci] <= cg && feedback.Time[ri + 1] - feedback.Time[ri] <= rg &&
                    !gapIndex.Overlaps(cursor, next);
                line.Points.Add(valid ? new DataPoint(cursor, command.Value[ci] - feedback.Value[ri]) : DataPoint.Undefined);
                if (next <= cursor) break;
                cursor = next;
            }
            model.Series.Add(line);
            if (SelectedReferenceResult is { Reference: MotionResponseReference.PositionRequest, PositionTolerance: { } tolerance })
            {
                foreach (var side in new[] { -1, 1 })
                    model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, Y = side * tolerance,
                        Color = OxyColors.Gray, LineStyle = LineStyle.Dot, Text = side > 0 ? "Positiemarge +" : "Positiemarge −" });
            }
        }
        else
        {
            model.Series.Add(EventSeries(command, "Setpoint", OxyColor.Parse("#2563EB"), start, end,
                5 * (_exportMotionResponse?.CommandSampleIntervalSeconds ?? 0),
                MeasurementGapSelection.ForChannel(_dataset?.ImportReport, command.Identity.Channel)));
            model.Series.Add(EventSeries(feedback, "Gemeten positie", OxyColor.Parse("#D97706"), start, end,
                5 * (_exportMotionResponse?.ResponseSampleIntervalSeconds ?? 0),
                MeasurementGapSelection.ForChannel(_dataset?.ImportReport, feedback.Identity.Channel)));
            if (measurement?.ResponseTarget is { } target)
                model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, Y = target, Color = OxyColors.Gray, LineStyle = LineStyle.Dot, Text = "Bewegingsgrens" });
        }
        if (measurement is not null)
        {
            var requestReference = SelectedReferenceResult?.Reference == MotionResponseReference.PositionRequest;
            if (requestReference && measurement.OriginCommandTimeSeconds is { } origin && origin < measurement.CommandTimeSeconds && origin >= start)
                model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = origin, Color = OxyColors.Gray, LineStyle = LineStyle.Dash, Text = "Setpointwijziging" });
            if (measurement.CommandTimeSeconds >= start)
                model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = measurement.CommandTimeSeconds, Color = OxyColor.Parse("#2563EB"),
                    Text = requestReference ? measurement.Outcome == MotionResponseOutcome.NoNewDirectionRequest ? "Setpointwijziging; geen bevestigde vraag" : "Positievraag" : "Setpointwijziging" });
            if (measurement.ReactionTimeSeconds is { } reaction)
                model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = reaction, Color = OxyColor.Parse("#059669"), Text = "Reactie" });
        }
        foreach (var gap in gaps.Where(g => g.StartSeconds < end && g.EndSeconds > start))
            model.Annotations.Add(new RectangleAnnotation { MinimumX = gap.StartSeconds, MaximumX = gap.EndSeconds,
                Fill = OxyColor.FromArgb(50, 220, 38, 38), Text = "Meetgat", Layer = AnnotationLayer.BelowSeries });
        return model;
    }

    private static StairStepSeries EventSeries(SignalSeries source, string title, OxyColor color, double start, double end, double gap,
        IReadOnlyList<MeasurementGap> importGaps)
    {
        var series = new StairStepSeries { Title = title, Color = color, StrokeThickness = 1.5, MarkerType = MarkerType.Circle, MarkerSize = 2 };
        var first = Math.Max(0, UpperBound(source.Time, start) - 1);
        var last = Math.Min(source.Time.Length, UpperBound(source.Time, end) + 1);
        var gapIndex = ReactionGapIndexes.GetValue(importGaps, static intervals => new ReactionGapIndex(intervals));
        for (var i = first; i < last; i++)
        {
            if (i > first && (source.Time[i] - source.Time[i - 1] > gap ||
                gapIndex.Overlaps(source.Time[i - 1], source.Time[i])))
                series.Points.Add(DataPoint.Undefined);
            series.Points.Add(new DataPoint(source.Time[i], source.Value[i]));
        }
        return series;
    }

    // Event selection runs on the UI thread. Index shared immutable import gaps once,
    // instead of scanning the complete log's gaps for every point in a short preview.
    private sealed class ReactionGapIndex
    {
        private readonly double[] _starts;
        private readonly double[] _maximumEnds;

        public ReactionGapIndex(IReadOnlyList<MeasurementGap> gaps)
        {
            var ordered = gaps.Where(gap => !double.IsNaN(gap.StartSeconds) && !double.IsNaN(gap.EndSeconds))
                .OrderBy(gap => gap.StartSeconds).ToArray();
            _starts = new double[ordered.Length];
            _maximumEnds = new double[ordered.Length];
            var maximumEnd = double.NegativeInfinity;
            for (var i = 0; i < ordered.Length; i++)
            {
                _starts[i] = ordered[i].StartSeconds;
                maximumEnd = Math.Max(maximumEnd, ordered[i].EndSeconds);
                _maximumEnds[i] = maximumEnd;
            }
        }

        public bool Overlaps(double start, double end)
        {
            var low = 0; var high = _starts.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_starts[middle] < end) low = middle + 1;
                else high = middle;
            }
            return low > 0 && _maximumEnds[low - 1] > start;
        }
    }
}
