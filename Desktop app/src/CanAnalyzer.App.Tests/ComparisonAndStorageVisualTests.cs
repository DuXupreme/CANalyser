using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.State;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class ComparisonAndStorageVisualTests
{
    [Fact]
    public Task ComparisonOverviewAndSignalWatchRenderWithResolvedBindings() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var errors = new StringWriter(); using var listener = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            var comparison = new ComparisonViewModel(() => []);
            for (var i = 0; i < 2; i++)
            {
                var points = Enumerable.Range(0, 15).Select(minute => new ComparisonTrendPoint(minute * 60, (minute + 1) * 60,
                    60, (80 - minute * 1.2 - i * 2) * 60)).ToArray();
                comparison.Bundles.Add(new(new()
                {
                    Path = i == 0 ? "Machine-1_ochtend.zip" : "Machine-1_middag.zip", SchemaVersion = 3, MachineId = "M01",
                    ClockQuality = "verified", StartUtc = DateTimeOffset.Parse("2026-10-27T08:00:00Z").AddHours(i * 4),
                    StartSeconds = 0, EndSeconds = 900, SourceIdentityComplete = true, SourceIds = ["source-" + i], FullDataset = true,
                    Completeness = "Complete", Metrics = [new("soc", "BMS.SOC [1] — tijdgemiddelde", "%", ComparisonAggregation.WeightedMean,
                        points.Average(p => p.Value), points.Sum(p => p.Integral), 900, "same-method", points, Minimum: 60, Maximum: 80)]
                }));
            }
            comparison.Machines.Add("M01"); comparison.SelectedMachine = "M01";
            Assert.Equal(2, comparison.Results.Count);
            var compareView = new ComparisonView { DataContext = comparison };
            await Render(compareView, "comparison.png");
            Assert.Equal(2, Find<DataGrid>(compareView).Count());

            var telemetry = new AnalyticsRegressionTests.Telemetry();
            var overview = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
            overview.OverviewStatus = "Volledige dataset · standaardanalyses · één joystick, twee actuatoren en twee CAN-kanalen.";
            overview.OverviewCards.Add(new("Hoe wordt de joystick bediend?", "63% neutraal; 18% kleine correcties; 7% bijna of volledig voluit.", "14 min 45 s gezamenlijke meetdekking; bevestigde kalibratie.", "Beschikbaar", 2));
            overview.OverviewCards.Add(new("Hoe reageert de linker actuator?", "Gemiddelde reactietijd 120 ms; P95 190 ms. 27 geaccepteerde stuuracties.", "Setpointwijzigingen; oorspronkelijke meetpunten.", "Beschikbaar", 1));
            overview.OverviewCards.Add(new("Hoe lang is de machine actief?", "9 min 12 s actief binnen 15 min meettijd.", "Activiteit volgens bevestigde grens; ontbrekende tijd apart.", "Beschikbaar", 4));
            overview.OverviewCards.Add(new("Wat weten we over de accuconfiguratie?", "Het aantal actieve accupacks is onbekend.", "Kies een gedocumenteerd tel-signaal of bevestig een aantal.", "Instelling nodig", 4));
            overview.OverviewCards.Add(new("Is de meetdata bruikbaar?", "Twee CAN-kanalen; loggerklok onbevestigd. 12 s ontbrekende dekking op kanaal 1.", "Opnametijd en ontvangsttijd blijven afzonderlijk.", "Gedeeltelijk", 3));
            var overviewView = new AnalysisOverviewView { DataContext = overview };
            await Render(overviewView, "analysis-overview.png");
            Assert.Equal(5, Find<Button>(overviewView).Count(button => Equals(button.Content, "Bekijk metingen en uitleg")));

            var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 42, "Actuator", "Position");
            var other = identity with { Channel = "2", MessageName = "BMS", SignalName = "SOC" };
            var samples = new[] { new DecodedSignalSample(0, 0, 1, identity, 120, 120, "mm"),
                new DecodedSignalSample(0, 1, 2, other, 82, 82, "%"), new DecodedSignalSample(4_000_000_000, 2, 3, identity, 125, 125, "mm") };
            using var data = new DatasetBuilder().Build([
                new RawCanFrame(0L, 42, 1, [120], "Rx", "1", false, 0, 1),
                new RawCanFrame(0L, 42, 1, [82], "Rx", "2", false, 1, 2),
                new RawCanFrame(4_000_000_000L, 42, 1, [125], "Rx", "1", false, 2, 3)], samples, [], new(0, 0, 2, 0, 0, ""));
            var watch = new BusmasterViewModel(new DisplayLimits());
            watch.ApplySignalWatchOptions(new SignalWatchOptions { Signals = [identity, other] });
            watch.LoadDataset(data); watch.SetWatchCursorAt(2); await watch.WaitForWatchValuesAsync();
            var watchView = new BusmasterView { DataContext = watch };
            Layout(watchView);
            Find<TabControl>(watchView).Single().SelectedIndex = 1;
            await Render(watchView, "signal-watch.png");
            Assert.Equal(2, watch.SignalWatchRows.Count);
            watch.ClearDataset(); overview.ClearDataset();
            listener.Flush();
            Assert.DoesNotContain("BindingExpression path error", errors.ToString());
            Assert.DoesNotContain("Cannot find source", errors.ToString());
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    });

    private static void Layout(FrameworkElement view)
    {
        view.Width = 1400; view.Height = 1000;
        view.Measure(new Size(1400, 1000)); view.Arrange(new Rect(0, 0, 1400, 1000)); view.UpdateLayout();
    }
    private static async Task Render(FrameworkElement view, string name)
    {
        Layout(view);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        view.UpdateLayout();
        var directory = Environment.GetEnvironmentVariable("CANALYSER_IMPLEMENTATION_VISUALS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(1400, 1000, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1400, 1000));
        bitmap.Render(background); bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); png.Save(file);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var match in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return match;
    }
}
