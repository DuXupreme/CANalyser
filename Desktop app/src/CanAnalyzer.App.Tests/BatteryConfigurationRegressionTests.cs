using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class BatteryConfigurationRegressionTests
{
    private static CanDataset Dataset(params SignalSeries[] series) => new()
    {
        RawFrames = [], DecodedSamples = [], MessageSummaries = [],
        SignalLabels = series.Select(s => s.Label).ToArray(),
        SignalSeriesByLabel = series.ToDictionary(s => s.Label), SignalSeriesByIdentity = series.ToDictionary(s => s.Identity),
        Diagnostics = new(0, 0, series.Length, 0, 0, "")
    };

    private static SignalSeries[] UsageSeries()
    {
        var time = Enumerable.Range(0, 101).Select(x => (double)x).ToArray();
        return [new SignalSeries("Motor.RPM", time, time.Select(_ => 1000d).ToArray()),
            new SignalSeries("BMS_STATUS_SOC", time, time.Select(t => 100 - t / 10).ToArray())];
    }

    [Fact]
    public Task CountsRequireExplicitMapping_AndManualCountDoesNotMultiplyCapacity() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = Dataset([.. UsageSeries(), new SignalSeries("BMS.ActivePackCount", [0, 50, 100], [2, 3, 3])]);
        var vm = new ActiveUsageViewModel(new AnalyticsRegressionTests.Telemetry());
        vm.LoadDataset(data);
        Assert.Equal(0, vm.BatteryCountSourceIndex);
        Assert.Null(vm.BatteryCountSignal);
        vm.ActivityThresholdConfirmed = true; vm.CapacityConfirmed = true;
        await vm.CalculateCommand.ExecuteAsync(null);
        var baseline = vm.ExportResult!;
        Assert.Equal(BatteryCountSource.Unknown, baseline.BatteryConfiguration!.Source);
        Assert.NotNull(baseline.ActiveRuntimeHours);

        vm.BatteryCountSourceIndex = 2;
        vm.ConfirmedBatteryCount = "3";
        vm.CapacityConfirmed = true;
        Assert.Null(vm.ExportResult);
        await vm.CalculateCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.ExportResult!.BatteryConfiguration!.ConfirmedManualCount);
        Assert.Equal("15", vm.CapacityKwh);
        Assert.Equal(baseline.ActiveRuntimeHours, vm.ExportResult.ActiveRuntimeHours);
        Assert.Contains("handmatig bevestigde", vm.BatteryCountSummary);
        vm.LoadDataset(data);
        Assert.Equal(0, vm.BatteryCountSourceIndex);
        Assert.Equal("", vm.ConfirmedBatteryCount);
        Assert.Empty(vm.BatteryCountIntervals);
    });

    [Fact]
    public Task MappedCountUsesOriginalSamplesOffDispatcher_AndBlocksChangedConfiguration() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var count = new SignalSeries(new SignalIdentity("CAN", CanFrameFormat.Classic, false, 1, "BMS", "ActivePackCount"), 300_000,
            () =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                return ([0L, 1_000_000_000L, 2_000_000_000L, 3_000_000_000L], new[] { 2d, 3d, 2d, 2d });
            }, _ => throw new InvalidOperationException("Count changes may not be sampled away"));
        using var data = Dataset([.. UsageSeries(), count]);
        var vm = new ActiveUsageViewModel(new AnalyticsRegressionTests.Telemetry());
        vm.LoadDataset(data);
        vm.BatteryCountSourceIndex = 1;
        vm.BatteryCountSignal = count.Label;
        await vm.CalculateCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult, vm.Status);
        Assert.Equal(2, vm.ExportResult!.BatteryConfiguration!.ObservedChanges);
        Assert.Null(vm.ExportResult.ActiveRuntimeHours);
        Assert.Contains("accuduurprognose onderdrukt", vm.BatteryCountSummary);
        Assert.DoesNotContain("SOC stijgt meer", vm.Notes);
        Assert.Equal("15", vm.CapacityKwh);
    });
}
