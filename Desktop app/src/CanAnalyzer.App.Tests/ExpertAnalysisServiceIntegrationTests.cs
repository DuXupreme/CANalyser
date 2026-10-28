using System.Numerics;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class ExpertAnalysisServiceIntegrationTests
{
    [Fact]
    public Task ReactionDetailRequiresReferenceConfirmationAndPreservesSourceIndicesInsideWindow() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = ReactionAnalysisRegressionTests.RampDataset(); var vm = Model();
        try
        {
            vm.LoadDataset(data); await ReactionAnalysisRegressionTests.Wait(vm);
            Assert.NotNull(vm.ReactionModuleResult!.Value!.SetpointChange.Value);
            Assert.Equal(ModuleStatus.NeedsConfiguration, vm.ReactionModuleResult.Value.PositionRequest.Status);
            Assert.Null(vm.ReactionModuleResult.Value.PositionRequest.Value);
            vm.PositionReferenceConfirmed = true;
            vm.UseJoystickTimeWindow = true; vm.JoystickTimeStartSeconds = .8; vm.JoystickTimeEndSeconds = 3;
            await vm.RecomputeCommand.ExecuteAsync(null);
            var result = vm.ReactionModuleResult!.Value!.PositionRequest;
            Assert.NotNull(result.Value); Assert.NotEmpty(result.ResultId);
            Assert.Equal(2.2, result.Coverage.RequestedSeconds, 8);
            var source = data.SignalSeriesByLabel["command"];
            Assert.All(result.Value!.Events, e =>
            {
                Assert.InRange(e.CommandTimeSeconds, .8, 3);
                Assert.Equal(e.CommandTimeSeconds, source.Time[e.CommandSourceIndex]);
            });
        }
        finally { await vm.CancelAndWaitAsync(); await vm.ActiveUsage.CancelAndWaitAsync(); vm.ClearDataset(); }
    });

    [Fact]
    public Task ContradictoryKnownUnitsBlockPositionAndTrackingInExpertViewEvenAfterConfirmation() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var source = ReactionAnalysisRegressionTests.RampDataset();
        var samples = source.SignalSeriesByIdentity.Values.Select((s, i) => new DecodedSignalSample(0, i, -1, s.Identity, s.Value[0], BigInteger.Zero,
            s.Label == "command" ? "mm" : "%")).ToArray();
        using var data = new CanDataset { RawFrames = [], DecodedSamples = samples, MessageSummaries = [],
            SignalLabels = source.SignalLabels, SignalSeriesByLabel = source.SignalSeriesByLabel,
            SignalSeriesByIdentity = source.SignalSeriesByIdentity, Diagnostics = source.Diagnostics };
        var vm = Model();
        try
        {
            vm.LoadDataset(data); await ReactionAnalysisRegressionTests.Wait(vm);
            vm.PositionReferenceConfirmed = true; await vm.RecomputeCommand.ExecuteAsync(null);
            var result = vm.ReactionModuleResult!.Value!;
            Assert.NotNull(result.SetpointChange.Value);
            Assert.Contains("unit_mismatch", result.PositionRequest.ReasonCodes);
            Assert.Null(result.PositionRequest.Value); Assert.Null(result.Tracking.Value);
            Assert.Equal("—", vm.MotionTrackingMeanText);
            Assert.Contains("eenheden", vm.MotionTrackingStatus);
        }
        finally { await vm.CancelAndWaitAsync(); await vm.ActiveUsage.CancelAndWaitAsync(); vm.ClearDataset(); }
    });

    private static JoystickAnalyticsViewModel Model()
    {
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        return new(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
    }
}
