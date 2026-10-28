using System.Numerics;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class ActiveUsageServiceIntegrationTests
{
    [Fact]
    public Task ExpertUsageUsesTheStandardServiceValuesAndPreservesWindowAndPresentation()
        => PlotRegressionTests.OnDispatcher(async () =>
        {
            using var dataset = Dataset();
            var vm = new ActiveUsageViewModel(new AnalyticsRegressionTests.Telemetry());
            try
            {
                vm.LoadDataset(dataset);
                vm.MinimumSeconds = "0"; vm.BridgeSeconds = "0"; vm.MaxGapSeconds = "2";
                vm.CapacityKwh = "20"; vm.BatteryCountSourceIndex = 2; vm.ConfirmedBatteryCount = "2";
                vm.ActivityThresholdConfirmed = true; vm.CapacityConfirmed = true;
                vm.SetTimeWindow(2, 6);
                await vm.CalculateCommand.ExecuteAsync(null);
                Assert.True(vm.HasResult, vm.Status);
                var detail = Assert.IsType<ModuleResult<ActiveUsageResult>>(vm.AnalysisResult);
                var profile = new AnalysisProfile
                {
                    Id = "expert-active-usage", DbcSha256 = dataset.DbcSha256, SourceLogSha256 = dataset.SourceLogSha256,
                    Usage = vm.CaptureProfile(), Battery = vm.CaptureBatteryProfile(), MaximumGapSeconds = 2
                };
                var standard = await Task.Run(() => new AnalysisRunService().Analyze(dataset, new()
                {
                    Profile = profile, Scope = "TimeWindow", StartSeconds = 2, EndSeconds = 6,
                    Modules = AnalysisModules.ActiveUsage | AnalysisModules.Battery
                }));
                Assert.Equal(standard.ActiveUsage.ResultId, detail.ResultId);
                Assert.Equal(standard.ActiveUsage.Status, detail.Status);
                Assert.Equal(standard.ActiveUsage.ReasonCodes, detail.ReasonCodes);
                Assert.Equal(standard.ActiveUsage.Coverage, detail.Coverage);
                Assert.Equal(standard.ActiveUsage.Value!.Intervals, detail.Value!.Intervals);
                Assert.Equal(standard.ActiveUsage.Value.ActiveSeconds, detail.Value.ActiveSeconds);
                Assert.Equal(standard.ActiveUsage.Value.NetKwh, detail.Value.TotalEnergy.NetKwh);
                Assert.Equal(standard.ActiveUsage.Value.ActiveRuntimeHours, detail.Value.ActiveRuntimeHours);
                Assert.Same(detail.Value, vm.ExportResult);
                Assert.NotEmpty(vm.TimeMetrics); Assert.NotEmpty(vm.EnergyMetrics); Assert.NotEmpty(vm.Timeline.Series);

                vm.SetTimeWindow(3, null);
                await vm.CalculateCommand.ExecuteAsync(null);
                Assert.True(vm.HasResult, vm.Status);
                Assert.Equal(3, vm.ExportResult!.StartSeconds);
                Assert.Equal(10, vm.ExportResult.EndSeconds);
            }
            finally { await vm.CancelAndWaitAsync(); vm.ClearDataset(); }
        });

    [Fact]
    public Task ExpertUsageHonorsCentralMixedMachineRestrictionAndClearsPreviousResult()
        => PlotRegressionTests.OnDispatcher(async () =>
        {
            using var dataset = Dataset();
            var vm = new ActiveUsageViewModel(new AnalyticsRegressionTests.Telemetry());
            try
            {
                vm.LoadDataset(dataset);
                await vm.CalculateCommand.ExecuteAsync(null);
                Assert.True(vm.HasResult, vm.Status);
                // Fixture-only source metadata; the production pipeline owns this internal setter.
                typeof(CanDataset).GetProperty(nameof(CanDataset.SourceFiles))!.SetValue(dataset, new SourceLogFile[]
                {
                    new("a.trc", "a.trc", null, null) { MachineId = "A", MachineAssignmentQuality = "embedded-installation" },
                    new("b.trc", "b.trc", null, null) { MachineId = "B", MachineAssignmentQuality = "embedded-installation" }
                });
                vm.LoadDataset(dataset);
                await vm.CalculateCommand.ExecuteAsync(null);
                Assert.False(vm.HasResult);
                Assert.Null(vm.ExportResult);
                Assert.Equal(ModuleStatus.MissingSignals, vm.AnalysisResult!.Status);
                Assert.Contains("mixed_machines", vm.AnalysisResult.ReasonCodes);
                Assert.Empty(vm.TimeMetrics); Assert.Empty(vm.Timeline.Series);
                Assert.Contains("gescheiden", vm.Status);
            }
            finally { await vm.CancelAndWaitAsync(); vm.ClearDataset(); }
        });

    private static CanDataset Dataset()
    {
        var activity = new SignalIdentity("1", CanFrameFormat.Classic, false, 10, "Motor", "RPM");
        var soc = new SignalIdentity("1", CanFrameFormat.Classic, false, 11, "Battery", "SOC");
        var timestamps = Enumerable.Range(0, 11).Select(i => i * 1_000_000_000L).ToArray();
        SignalSeries[] series = [new(activity, timestamps, timestamps.Select(_ => 1000d).ToArray()),
            new(soc, timestamps, Enumerable.Range(0, 11).Select(i => 100 - i * .1).ToArray())];
        return new()
        {
            RawFrames = timestamps.Select((time, index) => new RawCanFrame(time, 10, 1, [0], "DT", "1", false, index)).ToArray(),
            DecodedSamples = timestamps.SelectMany((time, index) => new[]
            {
                new DecodedSignalSample(time, index, index, activity, 1000, BigInteger.Zero, "rpm"),
                new DecodedSignalSample(time, index, index, soc, 100 - index * .1, BigInteger.Zero, "%")
            }).ToArray(),
            SignalSeriesByIdentity = series.ToDictionary(item => item.Identity),
            SignalSeriesByLabel = series.ToDictionary(item => item.Label), SignalLabels = series.Select(item => item.Label).ToArray(),
            MessageSummaries = [], Diagnostics = new(0, 0, 0, 0, 0, "synthetic"),
            DbcSha256 = new string('B', 64), SourceLogSha256 = new string('A', 64)
        };
    }
}
