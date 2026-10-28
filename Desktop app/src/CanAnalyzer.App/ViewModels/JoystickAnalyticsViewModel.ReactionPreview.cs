using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class JoystickAnalyticsViewModel
{
    private sealed record ReactionSourcePreview(DataPoint[] Command, DataPoint[] Feedback,
        IReadOnlyList<MeasurementGap> Gaps, double Start, double End)
    {
        public ReactionSourcePreview? FirstGapPreview { get; init; }
    }
    private ReactionSourcePreview? _reactionSourcePreview;
    public bool HasReactionSourceGap => _reactionSourcePreview?.Gaps.Count > 0;
    public string ReactionGapSummary => _reactionSourcePreview is not { } preview ? string.Empty :
        preview.Gaps.Count == 0 ? "Geen meetgaten in de bronpreview van dit tijdvenster." :
        $"{preview.Gaps.Count:N0} meetgat(en) in dit tijdvenster; {ReactionEvents.Count(e => e.Measurement.Outcome == MotionResponseOutcome.MeasurementGap):N0} stuuracties in deze categorie daardoor uitgesloten. " +
        "Geldige metingen blijven behouden. Bekijk het meetgat of pas Start/Einde bovenaan aan.";

    private void SetReactionSourcePreview(ReactionSourcePreview? preview)
    {
        _reactionSourcePreview = preview;
        OnPropertyChanged(nameof(HasReactionSourceGap));
        OnPropertyChanged(nameof(ReactionGapSummary));
        ShowReactionSourceGapCommand.NotifyCanExecuteChanged();
    }

    // Only display points are reduced. All numerical measurements above use the original samples.
    // Build on the analysis worker, so a full-log preview does not allocate full OxyPlot point lists on the dispatcher.
    private static ReactionSourcePreview PrepareReactionSourcePreview(SignalSeries command, SignalSeries feedback,
        MotionResponseResult result, (double? Start, double? End) window, ImportReport? report, CancellationToken token)
    {
        var start = window.Start ?? Math.Min(command.Time.FirstOrDefault(), feedback.Time.FirstOrDefault());
        var end = window.End ?? Math.Max(command.Time.LastOrDefault(), feedback.Time.LastOrDefault());
        var commandGaps = SourceGaps(command, result.CommandSampleIntervalSeconds);
        var feedbackGaps = SourceGaps(feedback, result.ResponseSampleIntervalSeconds);
        var gaps = MeasurementGapSelection.Union(commandGaps.Concat(feedbackGaps))
            .Where(g => g.StartSeconds < end && g.EndSeconds > start).ToArray();
        var commandIndex = new SignalRangeIndex(command.Time, command.Value, token);
        var feedbackIndex = new SignalRangeIndex(feedback.Time, feedback.Value, token);
        var commandGapIndex = new ReactionGapIndex(commandGaps);
        var feedbackGapIndex = new ReactionGapIndex(feedbackGaps);
        var preview = Create(start, end);
        if (gaps.Length > 0)
        {
            var firstGap = gaps[0];
            var padding = Math.Max(.2, Math.Min(2, (firstGap.EndSeconds - firstGap.StartSeconds) / 4));
            // Select the focused points from the original sources, not the whole-log overview.
            // Both selections reuse these worker-built indices; the UI only publishes bounded cached points.
            preview = preview with { FirstGapPreview = Create(Math.Max(start, firstGap.StartSeconds - padding),
                Math.Min(end, firstGap.EndSeconds + padding)) };
        }
        return preview;

        ReactionSourcePreview Create(double low, double high) => new(
            Points(commandIndex, commandGapIndex, low, high), Points(feedbackIndex, feedbackGapIndex, low, high),
            gaps.Where(g => g.StartSeconds < high && g.EndSeconds > low).ToArray(), low, high);

        IReadOnlyList<MeasurementGap> SourceGaps(SignalSeries source, double interval)
        {
            var missing = new List<MeasurementGap>(MeasurementGapSelection.ForChannel(report, source.Identity.Channel));
            if (interval > 0)
                for (var i = 1; i < source.Time.Length; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    if (source.Time[i] - source.Time[i - 1] > 5 * interval)
                        missing.Add(new(source.Time[i - 1], source.Time[i]));
                }
            return MeasurementGapSelection.Union(missing);
        }
        DataPoint[] Points(SignalRangeIndex sourceIndex, ReactionGapIndex gapIndex, double low, double high)
        {
            var (x, y) = sourceIndex.Select(low, high, 4000, token);
            var points = new List<DataPoint>(x.Length * 2);
            for (var i = 0; i < x.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (i > 0 && gapIndex.Overlaps(x[i - 1], x[i])) points.Add(DataPoint.Undefined);
                // Samples explicitly inside an unavailable interval are not presented as trustworthy observations.
                points.Add(gapIndex.Overlaps(x[i], x[i]) ? DataPoint.Undefined : new(x[i], y[i]));
            }
            return points.ToArray();
        }
    }

    private PlotModel BuildReactionSourcePreview(bool overlayOptions = false, ReactionSourcePreview? source = null)
    {
        if ((source ?? _reactionSourcePreview) is not { } preview) return EmptyPlot("Kies setpoint en feedback voor een bronpreview");
        var normalize = overlayOptions && DelayOverlayNormalize;
        var model = new PlotModel { Title = "Bronpreview · niet in tijd verschoven", TitleFontSize = 14,
            Subtitle = "Maximaal 4.000 geselecteerde bronpunten per signaal.\n‘Alle punten’ is niet beschikbaar voor deze bronpreview; geen reactietijd of verschuiving.", SubtitleFontSize = 11,
            IsLegendVisible = !overlayOptions || DelayOverlayShowLegend };
        PlotSourceMetadata.MarkReducedPreview(model);
        model.Legends.Add(new Legend { LegendPosition = LegendPosition.TopLeft, LegendPlacement = LegendPlacement.Outside, LegendOrientation = LegendOrientation.Horizontal });
        model.Axes.Add(Axis(AxisPosition.Bottom, "Tijd in log [s]", preview.Start, preview.End));
        model.Axes.Add(Axis(AxisPosition.Left, normalize ? "Genormaliseerde bronwaarde" : "Gedecodeerde signaalwaarde"));
        Add(preview.Command, "Setpoint", OxyColor.Parse("#2563EB"));
        Add(preview.Feedback, "Gemeten positie", OxyColor.Parse("#D97706"));
        foreach (var gap in preview.Gaps.Take(200))
            model.Annotations.Add(new RectangleAnnotation { MinimumX = gap.StartSeconds, MaximumX = gap.EndSeconds,
                Fill = OxyColor.FromArgb(50, 220, 38, 38), Text = "Meetgat", Layer = AnnotationLayer.BelowSeries });
        if (preview.Gaps.Count > 200) model.Subtitle += "; maximaal 200 gatmarkeringen, alle gaten blijven onderbroken";
        return model;

        void Add(DataPoint[] points, string title, OxyColor color)
        {
            LineSeries line = !overlayOptions || DelayOverlayStepPlot ? new StairStepSeries() : new LineSeries();
            line.Title = title; line.Color = color; line.StrokeThickness = 1.5;
            line.MarkerType = overlayOptions && DelayOverlaySampleMarkers ? MarkerType.Circle : MarkerType.None;
            line.MarkerSize = 2;
            var values = normalize ? points.Where(p => double.IsFinite(p.Y)).Select(p => p.Y).ToArray() : [];
            var (center, scale) = normalize && values.Length > 0 ? GetRobustCenterScale(values) : (0d, 1d);
            foreach (var point in points) line.Points.Add(double.IsFinite(point.X) ? new(point.X, (point.Y - center) / scale) : DataPoint.Undefined);
            model.Series.Add(line);
        }
    }

    [RelayCommand(CanExecute = nameof(HasReactionSourceGap))]
    private void ShowReactionSourceGap()
    {
        if (_reactionSourcePreview is not { Gaps.Count: > 0, FirstGapPreview: { } detail } preview) return;
        var gap = preview.Gaps[0];
        ReactionEventPlot = BuildReactionSourcePreview(source: detail);
        ReactionDetailText = $"Bronpreview rond meetgat {gap.StartSeconds:0.###}–{gap.EndSeconds:0.###} s. " +
            "Over deze onderbreking wordt geen reactietijd gemeten. Pas zo nodig Start/Einde bovenaan aan; Volledige log herstelt het hele analysevenster.";
    }
}
