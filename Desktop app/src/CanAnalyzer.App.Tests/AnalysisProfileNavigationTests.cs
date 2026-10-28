using System.Diagnostics;
using System.Numerics;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class AnalysisProfileNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FrontFirstInitializationKeepsRolePairAndAvailableReferenceConsistent(bool savedProfile) =>
        PlotRegressionTests.OnDispatcher(async () =>
        {
            using var data = Dataset();
            var vm = Model();
            try
            {
                vm.ClearDataset();
                Assert.Equal("Geen dataset geladen.", vm.StatusText);
                if (savedProfile)
                {
                    var profile = Profile(data);
                    vm.ConfigureProfiles([profile with { Actuators = [profile.Actuators[2], profile.Actuators[0], profile.Actuators[1]] }]);
                }
                var statuses = new List<string>();
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StatusText)) statuses.Add(vm.StatusText); };
                vm.LoadDataset(data);
                Assert.Equal(Signal(2, "ActualPos"), vm.CurrentProfile!.Actuators[0].Feedback);
                Assert.Equal("Voor", vm.SelectedReactionActuator);
                Assert.Equal(data.SignalSeriesByIdentity[Signal(2, "Setpoint")].Label, vm.SelectedCommandSignal);
                Assert.Equal(data.SignalSeriesByIdentity[Signal(2, "ActualPos")].Label, vm.SelectedResponseSignal);
                Assert.Equal(data.SignalSeriesByIdentity[Signal(0, "Setpoint")].Label, vm.ActuatorRanges[0].SetpointSignal);
                Assert.Equal(data.SignalSeriesByIdentity[Signal(0, "ActualPos")].Label, vm.SelectedActuatorLeftSignal);
                Assert.Equal(vm.ReactionReferences[savedProfile ? 1 : 0], vm.SelectedReactionReference);
                Assert.Equal(savedProfile, vm.PositionReferenceConfirmed);
                Assert.Contains(statuses, text => text.Contains("worden berekend"));
                Assert.DoesNotContain("Geen dataset geladen", vm.StatusText);
                await Wait(vm);
                Assert.Contains(vm.ReactionEvents, row => row.Accepted);
                Assert.False(vm.HasUnsavedProfileChanges);
                var roles = SnapshotRoles(vm);
                var explicitChoice = vm.ReactionReferences[savedProfile ? 0 : 1];
                vm.SelectedReactionReference = explicitChoice;
                await vm.RecomputeCommand.ExecuteAsync(null);
                Assert.Equal(explicitChoice, vm.SelectedReactionReference);
                Assert.Equal(savedProfile, vm.PositionReferenceConfirmed);
                vm.SelectedReactionActuator = "Links";
                Assert.Equal(data.SignalSeriesByIdentity[Signal(0, "Setpoint")].Label, vm.SelectedCommandSignal);
                Assert.Equal(roles, SnapshotRoles(vm));
            }
            finally { await Stop(vm); }
        });

    [Theory]
    [InlineData(1, "Rechts")]
    [InlineData(2, "Voor")]
    public Task OpeningAnotherActuatorCardPreservesEveryRoleAndUsesItsOwnRunFilter(int selected, string role) =>
        PlotRegressionTests.OnDispatcher(async () =>
        {
            using var data = Dataset();
            var profile = Profile(data);
            var vm = Model();
            try
            {
                vm.ConfigureProfiles([profile]); vm.LoadDataset(data); await Wait(vm);
                vm.SelectedReactionActuator = "Links";
                var before = SnapshotRoles(vm);
                Assert.False(vm.HasUnsavedProfileChanges);
                vm.InspectAnalysisCommand.Execute(new AnalysisOverviewCard("", "", "", "", 1, profile.Actuators[selected].Id));
                await Task.Delay(450); await Wait(vm);

                Assert.Equal(role, vm.SelectedReactionActuator);
                Assert.Equal(data.SignalSeriesByIdentity[profile.Actuators[selected].Command!].Label, vm.SelectedCommandSignal);
                Assert.Equal(data.SignalSeriesByIdentity[profile.Actuators[selected].Feedback!].Label, vm.SelectedResponseSignal);
                Assert.Equal(before, SnapshotRoles(vm));
                Assert.False(vm.HasUnsavedProfileChanges);
                var tracking = Assert.IsType<ActuatorAnalysisResult>(vm.ReactionModuleResult!.Value).Tracking;
                Assert.Contains(profile.Actuators[selected].RunSignal!, tracking.Inputs);
                Assert.DoesNotContain(profile.Actuators[0].RunSignal!, tracking.Inputs);
                Assert.NotNull(tracking.Value);
                Assert.True(tracking.Value!.CoveredSeconds > 0);
            }
            finally { await Stop(vm); }
        });

    [Fact]
    public Task DetailChangesAreVisibleAndRequireExplicitProfileConfirmation() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = Dataset(); var profile = Profile(data); var vm = Model();
        try
        {
            vm.ConfigureProfiles([profile]); vm.LoadDataset(data); await Wait(vm);
            var original = vm.CurrentProfile;
            AnalysisProfile? confirmed = null; vm.ProfileConfirmed += value => confirmed = value;
            Assert.False(vm.HasUnsavedProfileChanges);

            vm.UseJoystickTimeWindow = true; vm.JoystickTimeStartSeconds = .5; vm.JoystickTimeEndSeconds = 2;
            Assert.False(vm.HasUnsavedProfileChanges); // A view scope is intentionally absent from standard export.
            vm.ActuatorRanges[1].RunValue = 7;
            Assert.True(vm.HasUnsavedProfileChanges);
            Assert.Contains("machine-export", vm.ProfileChangeStatus);
            Assert.Contains("versie 3", vm.ProfileChangeStatus);
            Assert.Same(original, vm.CurrentProfile);
            Assert.Null(confirmed);

            // Opening a stroke card must not restore saved settings over an unsaved edit.
            vm.InspectAnalysisCommand.Execute(new AnalysisOverviewCard("", "", "", "", 2, profile.Actuators[1].Id));
            Assert.Equal(7, vm.ActuatorRanges[1].RunValue);
            Assert.True(vm.HasUnsavedProfileChanges);
            vm.ConfirmProfileCommand.Execute(null);
            Assert.NotNull(confirmed);
            Assert.Equal(7, confirmed!.Actuators.Single(a => a.Feedback == profile.Actuators[1].Feedback).RunValue);
            Assert.False(vm.HasUnsavedProfileChanges);

            vm.ActiveUsage.Threshold = "321";
            Assert.True(vm.HasUnsavedProfileChanges);
            var persisted = confirmed;
            vm.SelectedReactionActuator = "Voor";
            Assert.Same(persisted, vm.CurrentProfile); // Navigation does not implicitly save other settings.
            await vm.CancelAndWaitAsync(); await vm.ActiveUsage.CancelAndWaitAsync(); vm.ClearDataset();
            Assert.False(vm.HasUnsavedProfileChanges);
        }
        finally { await Stop(vm); }
    });

    private static (string?, string?, string?, double, double?, double?)[] SnapshotRoles(JoystickAnalyticsViewModel vm)
    {
        var feedback = new[] { vm.SelectedActuatorLeftSignal, vm.SelectedActuatorRightSignal, vm.SelectedActuatorFrontSignal };
        return vm.ActuatorRanges.Select((range, index) => (range.SetpointSignal, feedback[index], range.RunSignal,
            range.RunValue, range.ManualCenter, range.HalfRange)).ToArray();
    }

    private static JoystickAnalyticsViewModel Model()
    {
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        return new(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
    }

    private static async Task Wait(JoystickAnalyticsViewModel vm)
    {
        var clock = Stopwatch.StartNew();
        while (vm.IsBusy || vm.ActiveUsage.IsBusy)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20)); await Task.Delay(10);
        }
    }

    private static async Task Stop(JoystickAnalyticsViewModel vm)
    {
        await vm.CancelAndWaitAsync(); await vm.ActiveUsage.CancelAndWaitAsync(); vm.ClearDataset();
    }

    private static SignalIdentity Signal(int index, string signal) => new("1", CanFrameFormat.Classic, false,
        (uint)(index + 1), new[] { "ActuatorLeft", "ActuatorRight", "ActuatorFront" }[index], signal);

    private static AnalysisProfile Profile(CanDataset data) => AnalysisProfileResolver.Recognize(data) with
    {
        Id = "three-actuators", Name = "Vaste rollen", Confirmed = true, Version = 3,
        Actuators = Enumerable.Range(0, 3).Select(index => new ActuatorAnalysisProfile("actuator-" + index,
            new[] { "Links", "Rechts", "Voor" }[index], Signal(index, "Setpoint"), Signal(index, "ActualPos"))
        {
            RunSignal = Signal(index, "Run"), RunValue = index + 1, PositionReferenceConfirmed = true,
            Stroke = new() { CalibrationConfirmed = true, ManualCenter = 10 * index, HalfRange = 10 + index }
        }).ToArray()
    };

    private static CanDataset Dataset()
    {
        var frames = new List<RawCanFrame>(); var samples = new List<DecodedSignalSample>();
        for (var i = 0; i <= 300; i++)
        {
            var ns = i * 10_000_000L;
            frames.Add(new(ns, 1, 8, new byte[8], "Rx", "1", false));
            for (var role = 0; role < 3; role++)
            {
                foreach (var (name, value) in new[] { ("Setpoint", i < 100 ? 0d : 10d),
                             ("ActualPos", Math.Clamp((i - 110) / 10d, 0, 10)), ("Run", (double)role + 1) })
                    samples.Add(new(ns, samples.Count, -1, Signal(role, name), value, BigInteger.Zero, name == "Run" ? "" : "mm"));
            }
        }
        return new DatasetBuilder().Build(frames, samples, [], new(0, 0, 0, 0, 0, ""),
            sourceLogSha256: new string('a', 64), dbcSha256: new string('b', 64));
    }
}
