using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class AnalysisProfileWorkflowTests
{
    [Fact]
    public Task SavedProfileRestoresPhysicalCalibrationAndAutomaticRecalculation() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = Dataset();
        var vm = Model();
        try
        {
            vm.LoadDataset(data); await Wait(vm);
            Assert.NotNull(vm.SelectedJoystickXSignal);
            Assert.False(vm.UseJoystickCalibration);
            vm.FillPercentJoystickCalibrationCommand.Execute(null);
            vm.UseJoystickCalibration = true;
            await Task.Delay(500); await Wait(vm);
            Assert.Equal(5, vm.JoystickAmplitudeCards.Count);
            AnalysisProfile? saved = null;
            vm.ProfileConfirmed += p => saved = p;
            vm.ConfirmProfileCommand.Execute(null);
            Assert.NotNull(saved);
            Assert.True(saved!.Joysticks[0].CalibrationConfirmed);
            await vm.CancelAndWaitAsync(); await vm.ActiveUsage.CancelAndWaitAsync();
            vm.ClearDataset();
            Assert.Empty(vm.OverviewCards);
            Assert.Null(vm.CurrentProfile);
            vm.ConfigureProfiles([saved]);
            vm.LoadDataset(data); await Wait(vm);
            Assert.True(vm.UseJoystickCalibration);
            Assert.Equal(-100, vm.JoystickXMinimum);
            Assert.Equal(5, vm.JoystickAmplitudeCards.Count);
            Assert.Contains(vm.OverviewCards, c => c.Title.Contains("joystick") && c.State == "Beschikbaar");
        }
        finally { await vm.CancelAndWaitAsync(); await vm.ActiveUsage.CancelAndWaitAsync(); vm.ClearDataset(); }
    });

    [Fact]
    public Task ChangedWindowRecomputesWithoutClickAndPublishesOnlyLatestSelection() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = Dataset(); var vm = Model();
        try
        {
            vm.LoadDataset(data); await Wait(vm);
            var canBefore = vm.CanFrameRateModel;
            vm.UseJoystickTimeWindow = true;
            vm.JoystickTimeStartSeconds = .5; vm.JoystickTimeEndSeconds = 3.5;
            vm.JoystickTimeStartSeconds = 1; vm.JoystickTimeEndSeconds = 3;
            await Task.Delay(500); await Wait(vm);
            var exported = JsonSerializer.SerializeToElement(vm.CaptureExport(), AnalysisBundleExporter.JsonOptions);
            Assert.Equal(2, exported.GetProperty("Joystick").GetProperty("Result").GetProperty("CoveredSeconds").GetDouble(), 5);
            Assert.Equal(1, exported.GetProperty("Scope").GetProperty("JoystickTimeStartSeconds").GetDouble());
            Assert.DoesNotContain("COMPLETE", vm.StatusText);
            Assert.Same(canBefore, vm.CanFrameRateModel);
        }
        finally { await vm.CancelAndWaitAsync(); await vm.ActiveUsage.CancelAndWaitAsync(); vm.ClearDataset(); }
    });

    [Fact]
    public Task OverviewShowsMissingModulesAndCanRenderWithoutSignalSetup() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = ReactionAnalysisRegressionTests.RampDataset(); var vm = Model();
        try
        {
            vm.LoadDataset(data); await Wait(vm);
            Assert.Contains(vm.OverviewCards, c => c.State == "Signaal ontbreekt");
            var view = new AnalysisOverviewView { DataContext = vm, Width = 1000, Height = 700 };
            view.Measure(new(1000, 700)); view.Arrange(new(0, 0, 1000, 700)); view.UpdateLayout();
            Assert.True(view.ActualHeight > 0);
            vm.InspectAnalysisCommand.Execute(vm.OverviewCards[0]);
            Assert.Equal(vm.OverviewCards[0].DetailTabIndex, vm.SelectedAnalysisTabIndex);
        }
        finally { await vm.CancelAndWaitAsync(); await vm.ActiveUsage.CancelAndWaitAsync(); vm.ClearDataset(); }
    });

    [Fact]
    public Task ActuatorReferencesRoundTripAndFeedStandardStrokeModule() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var source = Dataset();
        var actuator = new SignalIdentity("1", CanFrameFormat.Classic, false, 2, "ActuatorLeft", "RealPos");
        var samples = source.DecodedSamples.ToList();
        foreach (var frame in source.RawFrames)
            foreach (var (name, value) in new[] { ("RealPos", 5d), ("ActualPos", 4d), ("Center", 0d), ("HalfRange", 10d), ("Run", 1d) })
                samples.Add(new(frame.TimestampNanoseconds, samples.Count, -1, actuator with { SignalName = name }, value, BigInteger.Zero, "mm"));
        using var data = new DatasetBuilder().Build(source.RawFrames, samples.OrderBy(s => s.TimestampNanoseconds).ToArray(), [], new(0, 0, 0, 0, 0, ""),
            sourceLogSha256: new string('a', 64), dbcSha256: new string('b', 64));
        var vm = Model();
        try
        {
            vm.LoadDataset(data); await Wait(vm);
            string Label(string name) => data.SignalSeriesByIdentity[actuator with { SignalName = name }].Label;
            var range = vm.ActuatorRanges[0];
            range.CenterSignal = Label("Center"); range.HalfRangeSignal = Label("HalfRange"); range.RunSignal = Label("Run");
            vm.PositionReferenceConfirmed = true;
            AnalysisProfile? saved = null; vm.ProfileConfirmed += p => saved = p;
            vm.ConfirmProfileCommand.Execute(null);
            Assert.NotNull(saved);
            var profile = JsonSerializer.Deserialize<AnalysisProfile>(JsonSerializer.Serialize(saved))!;
            var mapping = Assert.Single(profile.Actuators);
            Assert.True(mapping.Stroke!.CalibrationConfirmed);
            Assert.Equal("Center", mapping.Stroke.CenterSignal!.SignalName);
            Assert.Equal("Run", mapping.RunSignal!.SignalName);
            var run = await Task.Run(() => new AnalysisRunService().Analyze(data, new() { Profile = profile }));
            Assert.Equal(70, Assert.Single(run.Actuators).Value!.Stroke!.Value!.P99Percent!.Value, 8);
            await vm.CancelAndWaitAsync(); await vm.ActiveUsage.CancelAndWaitAsync(); vm.ClearDataset();
            vm.ConfigureProfiles([profile]); vm.LoadDataset(data); await Wait(vm);
            Assert.Equal(Label("Center"), vm.ActuatorRanges[0].CenterSignal);
            Assert.Equal(Label("HalfRange"), vm.ActuatorRanges[0].HalfRangeSignal);
            Assert.Equal(Label("Run"), vm.ActuatorRanges[0].RunSignal);
            Assert.Contains(vm.OverviewCards, c => c.Title.Contains("actuatorbereik") && c.State == "Beschikbaar");
        }
        finally { await vm.CancelAndWaitAsync(); await vm.ActiveUsage.CancelAndWaitAsync(); vm.ClearDataset(); }
    });

    private static JoystickAnalyticsViewModel Model()
    {
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        return new(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
    }

    private static async Task Wait(JoystickAnalyticsViewModel vm)
    {
        var timeout = Stopwatch.StartNew();
        while (vm.IsBusy || vm.ActiveUsage.IsBusy)
        {
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(15)); await Task.Delay(10);
        }
    }

    private static CanDataset Dataset()
    {
        var x = new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "Joystick", "pos_x");
        var y = x with { SignalName = "pos_y" };
        var frames = new List<RawCanFrame>(); var samples = new List<DecodedSignalSample>();
        for (var i = 0; i <= 400; i++)
        {
            var ns = i * 10_000_000L;
            frames.Add(new(ns, 1, 8, new byte[8], "Rx", "1", false));
            samples.Add(new(ns, i, -1, x, i is >= 100 and < 120 ? 20 : 0, BigInteger.Zero, "%"));
            samples.Add(new(ns, i, -1, y, 0, BigInteger.Zero, "%"));
        }
        return new DatasetBuilder().Build(frames, samples, [], new(0, 0, 0, 0, 0, ""),
            sourceLogSha256: new string('a', 64), dbcSha256: new string('b', 64));
    }
}
