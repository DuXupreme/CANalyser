using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class DisplayLimitsTests
{
    [Fact]
    public void NewWindowsStartWithAutomaticOverview()
    {
        Assert.True(new DisplayLimits().UseDownsampling);
        Assert.True(new PlotViewOptions().UseDownsampling);
        Assert.True(new AppSettings().LastPlotViewOptions.UseDownsampling);
    }

    [Fact]
    public Task ANewDatasetAlwaysLeavesTemporaryExactDetail() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        using var analysis = new AnalysisViewModel(new PlotModelBuilder(), null!, null!, null!,
            new XAxisSyncService(), telemetry, new ActiveUsageViewModel(telemetry),
            NullLogger<AnalysisViewModel>.Instance, new DisplayLimits());
        using var first = PlotRegressionTests.Dataset(); using var second = PlotRegressionTests.Dataset();
        analysis.LoadDataset(first); analysis.UseDownsampling = false;
        Assert.False(analysis.UseDownsampling);
        analysis.LoadDataset(second);
        Assert.True(analysis.UseDownsampling); Assert.Empty(analysis.PlotPanels);
        await analysis.CancelAndWaitAsync();
    });

    [Fact]
    public Task PointLimitsAreSharedButExactDetailBelongsOnlyToTheChosenWindow() =>
        PlotRegressionTests.OnDispatcher(async () =>
        {
            var limits = new DisplayLimits { UseDownsampling = true, MaxPointsPerTrace = 500 };
            var telemetry = new AnalyticsRegressionTests.Telemetry();
            var settings = new SettingsDiagnosticsViewModel(null!, null!, telemetry, limits);
            var analysis = new AnalysisViewModel(new PlotModelBuilder(), null!, null!, null!,
                new XAxisSyncService(), telemetry, new ActiveUsageViewModel(telemetry),
                NullLogger<AnalysisViewModel>.Instance, limits);
            using var dataset = PlotRegressionTests.Dataset();
            var source = dataset.SignalSeriesByLabel["test"];
            var values = source.Value.ToArray();
            analysis.LoadDataset(dataset);
            analysis.LoadPlotGroups([new PlotGroup { Signals = ["test"] }]);
            await PlotRegressionTests.Build(analysis);

            IReadOnlyList<PlotPanelModel> Panels() => new PlotModelBuilder().Build(dataset,
                [new PlotGroup { Signals = ["test"] }], analysis.CaptureViewOptions());
            var first = new PlotPanelsWindow(Panels(), analysis.CaptureViewOptions(), limits, true, true, null);
            var second = new PlotPanelsWindow(Panels(), analysis.CaptureViewOptions(), limits, true, true, null);
            try
            {
                first.MaxPointsPerTrace = 800;
                Assert.Equal(800, second.MaxPointsPerTrace);
                Assert.Equal(800, settings.DefaultMaxPointsPerTrace);
                Assert.Equal(800, analysis.CaptureViewOptions().MaxPointsPerTrace);
                Assert.InRange(((LineSeries)second.Panels[0].PlotModel.Series[0]).Points.Count, 1, 800);

                settings.DefaultMaxPointsPerTrace = 600;
                Assert.Equal(600, first.MaxPointsPerTrace);
                Assert.Equal(600, second.MaxPointsPerTrace);
                while (analysis.IsBusy || first.IsBusy || second.IsBusy) await Task.Delay(10);
                // Establish the actual viewport before requesting full precision.
                foreach (var panel in analysis.PlotPanels.Concat(first.Panels).Concat(second.Panels))
                    ((OxyPlot.IPlotModel)panel.PlotModel).Update(true);
                analysis.UseDownsampling = false;
                Assert.True(first.UseDownsampling);
                Assert.True(second.UseDownsampling);
                Assert.True(settings.DefaultUseDownsampling);
                while (analysis.IsBusy || first.IsBusy || second.IsBusy) await Task.Delay(10);
                foreach (var panel in analysis.PlotPanels.Concat(first.Panels).Concat(second.Panels))
                    ((OxyPlot.IPlotModel)panel.PlotModel).Update(true);
                Assert.InRange(((LineSeries)second.Panels[0].PlotModel.Series[0]).Points.Count, 1, 600);
                Assert.Equal(source.Time.Length, ((LineSeries)analysis.PlotPanels[0].PlotModel.Series[0]).Points.Count);
                Assert.Equal(values, source.Value);
                Assert.Same(source.Time, first.Panels[0].SeriesData[0].Time);

                var saved = new AppSettings();
                settings.WriteBackToSettings(saved);
                Assert.Equal(600, saved.LastPlotViewOptions.MaxPointsPerTrace);
                Assert.True(saved.LastPlotViewOptions.UseDownsampling);
            }
            finally { first.Close(); second.Close(); }
        });

    [Fact]
    public void RowLimit_ResetsPagingInBothTables_PreservesAllFramesAndSurvivesFilterReset()
    {
        var limits = new DisplayLimits { RawRowsPerPage = 3 };
        var raw = new RawFramesViewModel(new RawFrameFilterService(), limits);
        var bus = new BusmasterViewModel(limits);
        var settings = new SettingsDiagnosticsViewModel(null!, null!, new AnalyticsRegressionTests.Telemetry(), limits);
        var frames = Enumerable.Range(0, 11).Select(i => new RawCanFrame((long)i * 1000,
            1, 1, [(byte)i], "Rx", "1", false, i)).ToArray();
        using var dataset = new CanDataset
        {
            RawFrames = frames, DecodedSamples = [], MessageSummaries = [], SignalLabels = [],
            SignalSeriesByLabel = new Dictionary<string, SignalSeries>(),
            SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries>(),
            Diagnostics = new(0, 0, 0, 0, 0, "")
        };
        raw.LoadDataset(dataset); bus.LoadDataset(dataset);
        raw.NextPageCommand.Execute(null); bus.NextPageCommand.Execute(null);
        Assert.Equal(2, raw.PageNumber); Assert.Equal(2, bus.PageNumber);
        settings.DefaultRawMaxRows = 4;
        Assert.Equal(1, raw.PageNumber); Assert.Equal(1, bus.PageNumber);
        Assert.Equal(4, raw.FilteredFrames.Count); Assert.Equal(4, bus.Messages.Count);
        Assert.False(raw.PreviousPageCommand.CanExecute(null));
        Assert.False(bus.PreviousPageCommand.CanExecute(null));
        var seen = new List<long>();
        do
        {
            seen.AddRange(raw.FilteredFrames.Select(row => row.Source.FrameIndex));
            if (!raw.NextPageCommand.CanExecute(null)) break;
            raw.NextPageCommand.Execute(null);
        } while (true);
        Assert.Equal(frames.Select(frame => frame.FrameIndex), seen);
        raw.ResetFiltersCommand.Execute(null); bus.ResetFiltersCommand.Execute(null);
        Assert.Equal(4, raw.MaxRows); Assert.Equal(4, bus.MaxRows);
        Assert.Same(frames, dataset.RawFrames);
        var saved = new AppSettings();
        settings.WriteBackToSettings(saved);
        Assert.Equal(4, saved.LastRawFrameFilter.MaxRows);
    }

    [Fact]
    public void RowLimit_AfterRetiringDiskBackedDataset_KeepsTablesEmptyAndCanLoadReplacement()
    {
        var limits = new DisplayLimits { RawRowsPerPage = 2 };
        var raw = new RawFramesViewModel(new RawFrameFilterService(), limits);
        var bus = new BusmasterViewModel(limits);
        var settings = new SettingsDiagnosticsViewModel(null!, null!, new AnalyticsRegressionTests.Telemetry(), limits);
        using var store = new DiskBackedFrameStore();
        for (var i = 0; i < 5; i++)
            store.Append(new RawCanFrame((long)i * 1000, 1, 1, [(byte)i], "Rx", "1", false, i));
        store.Complete();
        using var dataset = new CanDataset
        {
            RawFrames = store, DecodedSamples = [], MessageSummaries = [], SignalLabels = [],
            SignalSeriesByLabel = new Dictionary<string, SignalSeries>(),
            SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries>(),
            Diagnostics = new(0, 0, 0, 0, 0, "")
        };
        raw.LoadDataset(dataset); bus.LoadDataset(dataset);
        raw.NextPageCommand.Execute(null); bus.NextPageCommand.Execute(null);
        Assert.Equal(2, raw.PageNumber); Assert.Equal(2, bus.PageNumber);
        Assert.NotNull(bus.SelectedMessage);

        // Retire the previous log before an import that may fail or be cancelled.
        raw.ClearDataset(); bus.ClearDataset(); dataset.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = store[0]; });
        Assert.Empty(raw.FilteredFrames); Assert.Empty(bus.Messages);
        Assert.Null(bus.SelectedMessage); Assert.Empty(bus.SelectedSignals);
        Assert.Empty(bus.SignalWatchRows);
        Assert.False(raw.PreviousPageCommand.CanExecute(null));
        Assert.False(raw.NextPageCommand.CanExecute(null));
        Assert.False(bus.PreviousPageCommand.CanExecute(null));
        Assert.False(bus.NextPageCommand.CanExecute(null));

        settings.DefaultRawMaxRows = 3;
        raw.ApplyFiltersCommand.Execute(null); bus.ApplyFiltersCommand.Execute(null);
        Assert.Equal(3, raw.MaxRows); Assert.Equal(3, bus.MaxRows);
        Assert.Equal(1, raw.PageNumber); Assert.Equal(1, bus.PageNumber);
        Assert.Empty(raw.FilteredFrames); Assert.Empty(bus.Messages);

        using var replacement = new CanDataset
        {
            RawFrames = [new RawCanFrame(0, 2, 1, [42], "Rx", "1", false, 0)],
            DecodedSamples = [], MessageSummaries = [], SignalLabels = [],
            SignalSeriesByLabel = new Dictionary<string, SignalSeries>(),
            SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries>(),
            Diagnostics = new(0, 0, 0, 0, 0, "")
        };
        raw.LoadDataset(replacement); bus.LoadDataset(replacement);
        Assert.Equal(2u, Assert.Single(raw.FilteredFrames).Source.Id);
        Assert.Equal(2u, Assert.Single(bus.Messages).Source.Id);
        Assert.Equal(3, raw.MaxRows); Assert.Equal(3, bus.MaxRows);
    }
}
