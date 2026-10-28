using System.Reflection;
using System.Text.Json;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using OxyPlot;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class CanDetailPresentationTests
{
    [Fact]
    public Task DetailTableUsesCoreEvidenceAndKeepsUnknownCoverageSerializable() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var frames = Enumerable.Range(0, 4).Select(i => new RawCanFrame((double)i, 1, 1, [0], "Rx", "1", false)).ToArray();
        using var dataset = new CanDataset
        {
            RawFrames = frames, DecodedSamples = [], MessageSummaries = [], SignalLabels = [],
            SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries>(), SignalSeriesByLabel = new Dictionary<string, SignalSeries>(),
            Diagnostics = new(0, 0, 0, 0, 0, ""), ImportReport = new("fixture", 4, 0, 4, 0, []) { Gaps = [new(1, 2)] }
        };
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
        vm.LoadDataset(dataset);
        while (vm.IsBusy) await Task.Delay(5);
        var result = JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Can").GetProperty("Result");
        Assert.Equal("can.detail", result.GetProperty("ModuleId").GetString());
        Assert.Equal(2, result.GetProperty("Coverage").GetProperty("ObservedSeconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("AverageBusLoadPercent").ValueKind);
        Assert.Contains(result.GetProperty("Bins").EnumerateArray(), bin => bin.GetProperty("FramesPerSecond").ValueKind == JsonValueKind.Null);
        Assert.Empty(vm.CanBusLoadModel.Series);
        await vm.CancelAndWaitAsync(); vm.ClearDataset();
    });

    [Fact]
    public void DisplayThinningCannotBridgeAOneBinObservationGap()
    {
        var values = Enumerable.Repeat(1d, 20_000).ToArray();
        values[10_001] = double.NaN;
        var method = typeof(JoystickAnalyticsViewModel).GetMethod("BuildCanTrendPlot", BindingFlags.NonPublic | BindingFlags.Static)!;
        var model = (PlotModel)method.Invoke(null, ["test", 0d, .1d, values, "frames/s", OxyColors.Blue, false])!;
        var points = Assert.IsType<LineSeries>(Assert.Single(model.Series)).Points;
        var separator = points.FindIndex(point => double.IsNaN(point.X));
        Assert.InRange(separator, 1, points.Count - 2);
        Assert.Equal(1000, points[separator - 1].X, 8);
        Assert.Equal(1000.2, points[separator + 1].X, 8);
    }
}
