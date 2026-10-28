using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using OxyPlot.Annotations;
using OxyPlot.Series;
using OxyPlot.Wpf;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class JoystickHistogramRenderingTests
{
    [Fact]
    public Task BoundHistogramRendersGuidesAfterImportAndRecomputation() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
        using var dataset = Dataset();
        var view = new PlotView { DataContext = vm, Width = 640, Height = 400 };
        view.SetBinding(PlotView.ModelProperty, new Binding(nameof(vm.RadiusHistogramModel)));
        var host = new Window { Content = view, Width = 660, Height = 440, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            // Keep the real WPF plot attached while the model is being published. Building
            // the view only after analysis finishes misses late-added annotation failures.
            host.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            vm.LoadDataset(dataset);
            await ReactionAnalysisRegressionTests.Wait(vm);
            await RenderAndAssert();
            Assert.False(vm.UseJoystickCalibration);

            var previous = vm.RadiusHistogramModel;
            vm.UseJoystickCalibration = true;
            vm.DeadzoneThreshold = .15;
            vm.SaturationThreshold = .85;
            await vm.RecomputeUsageCommand.ExecuteAsync(null);
            Assert.NotSame(previous, vm.RadiusHistogramModel);
            await RenderAndAssert();
        }
        finally
        {
            host.Close();
            await vm.CancelAndWaitAsync();
            await vm.ActiveUsage.CancelAndWaitAsync();
            vm.ClearDataset();
        }

        async Task RenderAndAssert()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            // A size-only WPF redraw renders the current annotations without another
            // data update. This exposes guides added after OxyPlot assigned the axes.
            view.Width = view.Width == 640 ? 620 : 640;
            view.Height = view.Height == 400 ? 380 : 400;
            view.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap(640, 400, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var model = vm.RadiusHistogramModel;
            Assert.Same(model, view.Model);
            Assert.True(model.PlotArea.Width > 0, "The real WPF plot has not rendered.");
            Assert.Null(model.GetLastPlotException());
            Assert.NotEmpty(Assert.Single(model.Series.OfType<RectangleBarSeries>()).Items);
            var guides = model.Annotations.OfType<LineAnnotation>().ToArray();
            Assert.Equal(2, guides.Length);
            Assert.Contains(guides, guide => guide.X == vm.DeadzoneThreshold);
            Assert.Contains(guides, guide => guide.X == vm.SaturationThreshold);
        }
    });

    private static CanDataset Dataset()
    {
        var seconds = Enumerable.Range(0, 1001).Select(i => i / 100d).ToArray();
        var timestamps = seconds.Select(t => (long)(t * 1_000_000_000)).ToArray();
        SignalSeries[] signals =
        [
            new(new SignalIdentity("1", CanFrameFormat.Classic, false, 0x301, "joystick", "pos_x"),
                timestamps, seconds.Select(t => Math.Sin(t)).ToArray()),
            new(new SignalIdentity("1", CanFrameFormat.Classic, false, 0x301, "joystick", "pos_y"),
                timestamps, seconds.Select(t => Math.Cos(t)).ToArray())
        ];
        return new() { RawFrames = [], DecodedSamples = [], MessageSummaries = [],
            SignalLabels = signals.Select(s => s.Label).ToArray(), SignalSeriesByLabel = signals.ToDictionary(s => s.Label),
            SignalSeriesByIdentity = signals.ToDictionary(s => s.Identity), Diagnostics = new(0, 0, 2, 0, 0, "") };
    }
}
