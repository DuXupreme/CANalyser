using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class JoystickCalibrationPresetTests
{
    [Theory]
    [InlineData(true, 100)]
    [InlineData(false, 1)]
    public Task PresetsFillBothAxesButAlwaysRequireFreshConfirmation(bool percent, double limit) =>
        PlotRegressionTests.OnDispatcher(() =>
        {
            var telemetry = new AnalyticsRegressionTests.Telemetry();
            var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry))
            {
                JoystickXMinimum = -25, JoystickXNeutral = 5, JoystickXMaximum = 50,
                JoystickYMinimum = 0, JoystickYNeutral = 100, JoystickYMaximum = 255,
                UseJoystickCalibration = true
            };
            var command = percent ? vm.FillPercentJoystickCalibrationCommand : vm.FillNormalizedJoystickCalibrationCommand;
            command.Execute(null);
            Assert.False(vm.UseJoystickCalibration);
            Assert.True(vm.JoystickSetupRequired);
            Assert.Equal(-limit, vm.JoystickXMinimum);
            Assert.Equal(0, vm.JoystickXNeutral);
            Assert.Equal(limit, vm.JoystickXMaximum);
            Assert.Equal(-limit, vm.JoystickYMinimum);
            Assert.Equal(0, vm.JoystickYNeutral);
            Assert.Equal(limit, vm.JoystickYMaximum);
            return Task.CompletedTask;
        });

    [Fact]
    public Task ApplyingPresetClearsPreviousPhysicalResultsAndCannotSilentlyReenableThem() =>
        PlotRegressionTests.OnDispatcher(async () =>
        {
            var time = Enumerable.Range(0, 101).Select(i => i * .01).ToArray();
            var x = new SignalSeries(new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "Joystick", "pos_x"),
                time.Select(t => (long)Math.Round(t * 1_000_000_000)).ToArray(), time.Select((_, i) => i is >= 10 and < 40 ? 20d : 0).ToArray());
            var y = new SignalSeries(new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "Joystick", "pos_y"),
                time.Select(t => (long)Math.Round(t * 1_000_000_000)).ToArray(), new double[time.Length]);
            using var data = new CanDataset
            {
                RawFrames = [], DecodedSamples = [], MessageSummaries = [],
                SignalLabels = [x.Label, y.Label],
                SignalSeriesByLabel = new Dictionary<string, SignalSeries> { [x.Label] = x, [y.Label] = y },
                SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries> { [x.Identity] = x, [y.Identity] = y },
                Diagnostics = new(0, 0, 1, 0, 0, "")
            };
            var telemetry = new AnalyticsRegressionTests.Telemetry();
            var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
            vm.LoadDataset(data);
            while (vm.IsBusy) await Task.Delay(5);
            vm.SelectedJoystickXSignal = x.Label;
            vm.SelectedJoystickYSignal = y.Label;
            vm.FillPercentJoystickCalibrationCommand.Execute(null);
            vm.UseJoystickCalibration = true;
            await vm.RecomputeUsageCommand.ExecuteAsync(null);
            Assert.Equal(5, vm.JoystickAmplitudeCards.Count);
            Assert.Equal("1", vm.JoystickCorrectionCountText);

            vm.FillNormalizedJoystickCalibrationCommand.Execute(null);
            Assert.False(vm.UseJoystickCalibration);
            Assert.Empty(vm.JoystickAmplitudeCards);
            Assert.Empty(vm.JoystickInputRows);
            await vm.RecomputeUsageCommand.ExecuteAsync(null);
            Assert.Empty(vm.JoystickAmplitudeCards);
            Assert.Empty(vm.JoystickInputRows);
            Assert.Equal("Kalibratie nodig", vm.JoystickCorrectionCountText);
            Assert.True(vm.JoystickSetupRequired);
        });
}
