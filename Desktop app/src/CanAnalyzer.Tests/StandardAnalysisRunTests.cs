using System.Numerics;
using System.Text.Json;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class StandardAnalysisRunTests
{
    [Fact]
    public void InteractiveDetailsShareStandardIdentityCoverageAndNumericsWithoutAddingSamplesToStandardContract()
    {
        using var dataset = Fixture();
        var request = new AnalysisRunRequest { Modules = AnalysisModules.Joystick | AnalysisModules.Actuators,
            Scope = "TimeWindow", StartSeconds = .2, EndSeconds = 2, Profile = AnalysisProfileResolver.Recognize(dataset) };
        var service = new AnalysisRunService();
        var standard = service.Analyze(dataset, request);
        var details = service.AnalyzeDetails(dataset, request);
        Assert.Equal(JsonSerializer.Serialize(standard), JsonSerializer.Serialize(details.Summary));
        foreach (var result in details.Joysticks)
        {
            var summary = standard.Joysticks.Single(s => s.SubjectId == result.SubjectId);
            Assert.Equal(summary.ResultId, result.ResultId);
            Assert.Equal(summary.Coverage, result.Coverage);
            Assert.Equal(summary.Status, result.Status);
            Assert.Equal(summary.Value!.CoveredSeconds, result.Value!.CoveredSeconds);
            Assert.NotEmpty(result.Value.Points);
        }
        Assert.DoesNotContain("\"Points\"", JsonSerializer.Serialize(standard));
    }

    [Fact]
    public void SeriesOnlyDatasetAndOpenTimeWindowUseOriginalSourceBounds()
    {
        using var original = Fixture();
        using var dataset = new CanDataset { RawFrames = [], DecodedSamples = [], MessageSummaries = [],
            SignalLabels = original.SignalLabels, SignalSeriesByLabel = original.SignalSeriesByLabel,
            SignalSeriesByIdentity = original.SignalSeriesByIdentity, Diagnostics = original.Diagnostics };
        var run = new AnalysisRunService().AnalyzeDetails(dataset, new() { Scope = "TimeWindow", StartSeconds = .5, Modules = AnalysisModules.Joystick });
        Assert.Equal(.5, run.Summary.StartSeconds);
        Assert.Equal(dataset.SignalSeriesByIdentity.Values.Max(s => s.Time[^1]), run.Summary.EndSeconds);
        Assert.All(run.Joysticks, r => Assert.True(r.Value!.CoveredSeconds > 0));
    }

    [Fact]
    public void DetailValuesRetainSocButNeverSuggestEnergyOrForecastFromUnconfirmedCapacity()
    {
        using var dataset = Fixture();
        var soc = dataset.SignalSeriesByIdentity.Keys.Single(i => i.SignalName == "SOC");
        var profile = new AnalysisProfile { DbcSha256 = dataset.DbcSha256, Battery = new(null, 2),
            Usage = new() { Activity = soc, Soc = soc, ThresholdConfirmed = true,
                Options = new() { ActivityThreshold = 1, MinimumActivitySeconds = 0, BridgePauseSeconds = 0 } } };
        var detail = new AnalysisRunService().AnalyzeDetails(dataset, new() { Profile = profile, Modules = AnalysisModules.ActiveUsage });
        var result = detail.ActiveUsage.Value!;
        Assert.False(result.EnergyAvailable);
        Assert.Equal("capacity_unconfirmed", result.ForecastUnavailableReason);
        Assert.Null(result.TotalEnergy.NetKwh);
        Assert.All(result.EnergyByState.Values, energy => Assert.Null(energy.NetKwh));
        Assert.Null(result.ActiveRuntimeHours); Assert.Null(result.MixedRuntimeHours);
        Assert.Null(result.RemainingActiveHours); Assert.Null(result.RemainingMixedHours);
        Assert.True(result.TotalEnergy.CoveredSeconds > 0);
        Assert.Contains("capacity_unconfirmed", detail.ActiveUsage.ReasonCodes);
        Assert.Equal(ModuleStatus.Partial, detail.ActiveUsage.Status);
    }

    [Fact]
    public void StandardRunIncludesEveryRecognizedSubjectAndChannelWithoutRawJoystickPoints()
    {
        using var dataset = Fixture();
        var service = new AnalysisRunService();
        var run = service.Analyze(dataset, new());
        Assert.Equal("FullDataset", run.Scope);
        Assert.Equal(2, run.Joysticks.Count);
        Assert.Equal(2, run.Actuators.Count);
        Assert.Equal(new[] { "1", "2" }, run.CanChannels.Select(r => r.SubjectId));
        Assert.All(run.Joysticks, r => { Assert.NotNull(r.Value); Assert.Equal(ModuleStatus.Partial, r.Status); Assert.False(r.Value!.IsCalibrated); });
        Assert.All(run.Actuators, r =>
        {
            Assert.NotNull(r.Value?.SetpointChange.Value);
            Assert.Equal(ModuleStatus.NeedsConfiguration, r.Value!.PositionRequest.Status);
        });
        Assert.DoesNotContain("\"Points\"", JsonSerializer.Serialize(run));
        Assert.Equal(ModuleStatus.NeedsConfiguration, run.BatteryConfiguration.Status);
    }

    [Fact]
    public void ConfirmedReferencesProduceSeparateMeasurementsAndPhysicalJoystickCategories()
    {
        using var dataset = Fixture();
        var recognized = AnalysisProfileResolver.Recognize(dataset);
        var profile = recognized with
        {
            Confirmed = true,
            Joysticks = recognized.Joysticks.Select(j => j with { CalibrationConfirmed = true,
                XCalibration = new(-100, 0, 100), YCalibration = new(-100, 0, 100) }).ToArray(),
            Actuators = recognized.Actuators.Select(a => a with { PositionReferenceConfirmed = true }).ToArray()
        };
        var run = new AnalysisRunService().Analyze(dataset, new() { Profile = profile });
        Assert.All(run.Joysticks, r => Assert.Equal(5, r.Value!.AmplitudeBands.Count));
        Assert.All(run.Actuators, r =>
        {
            Assert.Equal(MotionResponseReference.SetpointChange, r.Value!.SetpointChange.Value!.Reference);
            Assert.Equal(MotionResponseReference.PositionRequest, r.Value.PositionRequest.Value!.Reference);
            Assert.NotNull(r.Value.Tracking.Value);
        });
    }

    [Fact]
    public void ProfileReuseIsScopedAndChangedDefinitionOnlyInvalidatesAffectedCalibration()
    {
        using var old = Fixture();
        old.SignalDefinitionHashes = old.SignalSeriesByIdentity.Keys.ToDictionary(i => i, _ => "original");
        var recognized = AnalysisProfileResolver.Recognize(old);
        var saved = recognized with { Confirmed = true,
            Joysticks = recognized.Joysticks.Select(j => j with { CalibrationConfirmed = true,
                XCalibration = new(-100, 0, 100), YCalibration = new(-100, 0, 100) }).ToArray() };
        var roundTrip = JsonSerializer.Deserialize<AnalysisProfile>(JsonSerializer.Serialize(saved))!;
        Assert.True(AnalysisProfileResolver.IsApplicable(roundTrip, old));
        using var changed = Fixture(dbc: new string('c', 64));
        var changedId = saved.Joysticks[0].X!;
        changed.SignalDefinitionHashes = changed.SignalSeriesByIdentity.Keys.ToDictionary(i => i, i => i == changedId ? "changed-scale" : "original");
        var resolved = AnalysisProfileResolver.Resolve(changed, [roundTrip]);
        Assert.False(resolved.Joysticks.Single(j => j.X == changedId).CalibrationConfirmed);
        Assert.True(resolved.Joysticks.Single(j => j.X != changedId).CalibrationConfirmed);
        using var differentSource = Fixture(source: new string('d', 64));
        Assert.False(AnalysisProfileResolver.Resolve(differentSource, [saved]).Confirmed);
    }

    [Fact]
    public void NoMappingAndUnconfirmedCapacityRemainExplicitInsteadOfZeroOrDefaultEnergy()
    {
        using var dataset = Fixture();
        var soc = dataset.SignalSeriesByIdentity.Keys.Single(i => i.SignalName == "SOC");
        var run = new AnalysisRunService().Analyze(dataset, new() { Profile = new AnalysisProfile
        {
            DbcSha256 = dataset.DbcSha256,
            Usage = new() { Soc = soc, Options = new() { Method = ActivityDetectionMethod.SocSlope } }
        }});
        Assert.Equal(ModuleStatus.MissingSignals, Assert.Single(run.Joysticks).Status);
        Assert.Null(run.Joysticks[0].Value);
        Assert.NotEmpty(run.Joysticks[0].ReasonCodes);
        Assert.NotNull(run.ActiveUsage.Value);
        Assert.Null(run.ActiveUsage.Value!.NetKwh);
        Assert.Null(run.ActiveUsage.Value.ConfirmedCapacityKwh);
    }

    [Fact]
    public void CancellationAndNonFullDatasetRequestsCannotProduceAPartialSuccess()
    {
        using var dataset = Fixture();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new AnalysisRunService().Analyze(dataset, new(), cancellation.Token));
        Assert.Throws<ArgumentException>(() => new AnalysisRunService().Analyze(dataset, new() { Scope = "VisiblePlot" }));
    }

    [Fact]
    public void NumericalJoystickAnalysisNeverInvokesSampledLoader()
    {
        var x = new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "Joystick", "pos_x");
        var y = x with { SignalName = "pos_y" };
        var timestamps = Enumerable.Range(0, 300_001).Select(i => i * 1_000_000L).ToArray();
        var values = timestamps.Select((_, i) => i == 150_000 ? .2 : 0d).ToArray();
        SignalSeries Make(SignalIdentity id) => new(id, timestamps.Length,
            () => (timestamps, values), _ => throw new InvalidOperationException("Numerical downsampling is forbidden"));
        var series = new[] { Make(x), Make(y) };
        using var dataset = new CanDataset { RawFrames = [Frame(0, "1"), Frame(300_000_000_000L, "1")],
            DecodedSamples = [], MessageSummaries = [], Diagnostics = new(0, 0, 0, 0, 0, ""),
            SignalLabels = series.Select(s => s.Label).ToArray(), SignalSeriesByLabel = series.ToDictionary(s => s.Label),
            SignalSeriesByIdentity = series.ToDictionary(s => s.Identity) };
        var profile = AnalysisProfileResolver.Recognize(dataset);
        profile = profile with { Joysticks = profile.Joysticks.Select(j => j with {
            CalibrationConfirmed = true, XCalibration = new(-1, 0, 1), YCalibration = new(-1, 0, 1) }).ToArray() };
        var run = new AnalysisRunService().Analyze(dataset, new() { Profile = profile });
        Assert.Equal(1, run.Joysticks[0].Value!.InputEpisodes.Count(e => e.IsShortCorrection));
        Assert.Equal(.001, run.Joysticks[0].Value!.AmplitudeBands[1].Seconds, 6);
    }

    [Fact]
    public void TargetedWindowOnlyReturnsRequestedModulesAndKeepsTheirIdentityStable()
    {
        using var dataset = Fixture();
        var service = new AnalysisRunService();
        var request = new AnalysisRunRequest { Scope = "TimeWindow", StartSeconds = 1, EndSeconds = 2,
            Modules = AnalysisModules.Joystick | AnalysisModules.Can };
        var first = service.Analyze(dataset, request);
        var repeated = service.Analyze(dataset, request);
        Assert.Equal("TimeWindow", first.Scope);
        Assert.Empty(first.Actuators);
        Assert.Equal(ModuleStatus.NotRequested, first.ActiveUsage.Status);
        Assert.Equal(ModuleStatus.NotRequested, first.BatteryConfiguration.Status);
        Assert.Equal(ModuleStatus.NotRequested, first.Quality.Status);
        Assert.All(first.Joysticks, r => Assert.Equal(1, r.Value!.CoveredSeconds, 8));
        Assert.All(first.CanChannels, r => { Assert.Equal(202, r.Value!.FrameCount); Assert.Equal(1, r.Value.StartSeconds); Assert.Equal(2, r.Value.EndSeconds); });
        Assert.Equal(first.Joysticks[0].ResultId, repeated.Joysticks[0].ResultId);
        Assert.NotEmpty(first.Joysticks[0].ValueUnits);
        Assert.NotEqual(first.Joysticks[0].ResultId, service.Analyze(dataset, request with { EndSeconds = 3 }).Joysticks[0].ResultId);
        Assert.Throws<ArgumentException>(() => service.Analyze(dataset, request with { Scope = "FullDataset" }));
    }

    [Fact]
    public void ReactionWindowCannotUseFutureFeedbackForAcceptance()
    {
        using var dataset = Fixture();
        var profile = AnalysisProfileResolver.Recognize(dataset);
        profile = profile with { Actuators = profile.Actuators.Select(a => a with { PositionReferenceConfirmed = true }).ToArray() };
        var run = new AnalysisRunService().Analyze(dataset, new() { Profile = profile,
            Scope = "TimeWindow", StartSeconds = .9, EndSeconds = 1.1, Modules = AnalysisModules.Actuators });
        Assert.All(run.Actuators, a =>
        {
            Assert.Equal(0, a.Value!.SetpointChange.Value!.AllStatistics.MatchedEventCount);
            Assert.Equal("s", a.Value.SetpointChange.Unit);
            Assert.Equal("mm", a.Value.Tracking.Unit);
            Assert.InRange(a.Value.Tracking.Coverage.ObservedSeconds, 0, .200001);
            Assert.All(a.Value.SetpointChange.Value.Events, e => Assert.InRange(e.CommandTimeSeconds, .9, 1.1));
        });
        var after = new AnalysisRunService().Analyze(dataset, new() { Profile = profile,
            Scope = "TimeWindow", StartSeconds = 2, EndSeconds = 3, Modules = AnalysisModules.Actuators });
        Assert.All(after.Actuators, a => Assert.Empty(a.Value!.SetpointChange.Value!.Events));
    }

    [Fact]
    public void UnverifiedMachineLabelsDoNotAuthorizeProfileReuse()
    {
        using var dataset = Fixture();
        dataset.SourceFiles = [new("one", "one", null, null) { MachineId = "M1", InstallationId = "I1", MachineAssignmentQuality = "label" }];
        var profile = AnalysisProfileResolver.Recognize(dataset) with { Confirmed = true };
        Assert.Null(profile.MachineId);
        Assert.Null(profile.InstallationId);
        using var other = Fixture(source: new string('e', 64));
        other.SourceFiles = dataset.SourceFiles;
        Assert.False(AnalysisProfileResolver.IsApplicable(profile, other));
    }

    [Fact]
    public void InternalLoggerFramesDoNotChangeMachineTrafficOrAnalysisRange()
    {
        using var dataset = new DatasetBuilder().Build(
            [Frame(0, "internal"), Frame(1_000_000_000, "1"), Frame(2_000_000_000, "1"), Frame(99_000_000_000, "internal")],
            [], [], new(0, 0, 0, 0, 0, ""),
            importReport: new("fixture", 4, 0, 4, 0, []) { InternalDiagnosticChannels = ["internal"] });
        var run = new AnalysisRunService().Analyze(dataset, new());
        Assert.Equal(1, run.StartSeconds); Assert.Equal(2, run.EndSeconds);
        var can = Assert.Single(run.CanChannels);
        Assert.Equal("1", can.SubjectId); Assert.Equal(2, can.Value!.FrameCount);
        Assert.NotNull(run.Quality.Value!.LoggerDiagnostics);
    }

    [Fact]
    public void ChangedActuatorScalingDoesNotKeepAnAbsoluteThresholdOrPositionConfirmation()
    {
        using var old = Fixture();
        old.SignalDefinitionHashes = old.SignalSeriesByIdentity.Keys.ToDictionary(i => i, _ => "original");
        var saved = AnalysisProfileResolver.Recognize(old);
        saved = saved with { Confirmed = true, Actuators = saved.Actuators.Select(a => a with { PositionReferenceConfirmed = true,
            ResponseOptions = new() { ResponseThresholdAbsolute = 4, PositionToleranceAbsolute = 2 } }).ToArray() };
        using var changed = Fixture(dbc: new string('c', 64));
        var affected = saved.Actuators[0].Feedback;
        changed.SignalDefinitionHashes = changed.SignalSeriesByIdentity.Keys.ToDictionary(i => i, i => i == affected ? "scale-changed" : "original");
        var profile = AnalysisProfileResolver.Resolve(changed, [saved]);
        Assert.Null(profile.Actuators[0].Feedback);
        Assert.Null(profile.Actuators[0].ResponseOptions.ResponseThresholdAbsolute);
        Assert.False(profile.Actuators[0].PositionReferenceConfirmed);
        Assert.True(profile.Actuators[1].PositionReferenceConfirmed);
        Assert.Equal(4, profile.Actuators[1].ResponseOptions.ResponseThresholdAbsolute);
    }

    [Fact]
    public void UsageOnlyPlanStillChecksPackChangesBeforePublishingRuntime()
    {
        using var original = Fixture();
        var count = new SignalIdentity("1", CanFrameFormat.Classic, false, 4, "Battery", "ActivePacks");
        var samples = original.DecodedSamples.ToList();
        for (var i = 0; i <= 400; i++) samples.Add(new(i * 10_000_000L, samples.Count, -1, count, i < 200 ? 2 : 3, BigInteger.Zero));
        using var dataset = new DatasetBuilder().Build(original.RawFrames, samples.OrderBy(s => s.TimestampNanoseconds).ToArray(), [], new(0, 0, 0, 0, 0, ""));
        var soc = dataset.SignalSeriesByIdentity.Keys.Single(i => i.SignalName == "SOC");
        var profile = new AnalysisProfile { DbcSha256 = dataset.DbcSha256, Battery = new(count, null),
            Usage = new() { Activity = soc, Soc = soc, ThresholdConfirmed = true, CapacityConfirmed = true,
                Options = new() { ActivityThreshold = 1, MinimumActivitySeconds = 0, BridgePauseSeconds = 0 } } };
        var run = new AnalysisRunService().Analyze(dataset, new() { Profile = profile, Modules = AnalysisModules.ActiveUsage });
        Assert.NotNull(run.ActiveUsage.Value);
        Assert.True(run.ActiveUsage.Value!.ActiveSeconds > 0);
        Assert.Null(run.ActiveUsage.Value.ActiveRuntimeHours);
        Assert.Contains("battery_configuration_changed", run.ActiveUsage.ReasonCodes);
        Assert.Equal(ModuleStatus.NotRequested, run.BatteryConfiguration.Status);
        var fixedPacks = new AnalysisRunService().Analyze(dataset, new() { Profile = profile with { Battery = new(null, 2) }, Modules = AnalysisModules.ActiveUsage });
        Assert.NotNull(fixedPacks.ActiveUsage.Value!.ActiveRuntimeHours);
    }

    [Fact]
    public void CanCycleStatisticsDoNotBridgeAnExplicitSmallOmission()
    {
        using var dataset = new DatasetBuilder().Build(
            [Frame(0, "1"), Frame(100_000_000, "1"), Frame(200_000_000, "1"), Frame(300_000_000, "1")],
            [], [], new(0, 0, 0, 0, 0, ""), importReport: new("fixture", 4, 0, 4, 0, []) { Gaps = [new(.1, .2)] });
        var result = new AnalysisRunService().Analyze(dataset, new() { Modules = AnalysisModules.Can });
        var channel = Assert.Single(result.CanChannels);
        Assert.Equal(2, Assert.Single(channel.Value!.Streams).CycleCount);
        Assert.Equal(.2, channel.Coverage.ObservedSeconds, 8);
    }

    [Fact]
    public void ChannelCoverageKeepsUnobservedWindowEdgesInItsDenominator()
    {
        using var dataset = new DatasetBuilder().Build([Frame(10_000_000_000, "1"), Frame(20_000_000_000, "1")],
            [], [], new(0, 0, 0, 0, 0, ""));
        var result = new AnalysisRunService().Analyze(dataset, new() { Modules = AnalysisModules.Can, Scope = "TimeWindow", StartSeconds = 0, EndSeconds = 60,
            Profile = AnalysisProfileResolver.Recognize(dataset) with { CanBitratesConfirmed = true } });
        var channel = Assert.Single(result.CanChannels);
        Assert.Equal(60, channel.Coverage.RequestedSeconds);
        Assert.Equal(10, channel.Coverage.ObservedSeconds);
        Assert.Equal(ModuleStatus.Partial, channel.Status);
        Assert.Contains("coverage_incomplete", channel.ReasonCodes);
    }

    [Fact]
    public void ConfirmedStrokeReferencesProduceExactCoverageEvenWithoutACommand()
    {
        using var dataset = Fixture();
        var profile = AnalysisProfileResolver.Recognize(dataset);
        profile = profile with { Actuators = profile.Actuators.Select(a => a with { Command = null,
            Stroke = new() { CalibrationConfirmed = true, ManualCenter = 50, HalfRange = 50 } }).ToArray() };
        var run = new AnalysisRunService().Analyze(dataset, new() { Profile = profile, Modules = AnalysisModules.Actuators });
        Assert.All(run.Actuators, a =>
        {
            Assert.Equal(ModuleStatus.MissingSignals, a.Value!.SetpointChange.Status);
            Assert.Equal(ModuleStatus.Available, a.Value.Stroke!.Status);
            Assert.Equal("%", a.Value.Stroke.Unit);
            Assert.Equal(4, a.Value.Stroke.Value!.CoveredSeconds, 8);
            Assert.Equal(0, a.Value.Stroke.Value.SetpointCoveredSeconds);
            Assert.Equal(0, a.Value.Stroke.Value.MinimumPercent);
            Assert.Equal(100, a.Value.Stroke.Value.MaximumPercent);
        });
    }

    [Fact]
    public void AmbiguousJoystickRetainsConcreteCandidatesWithoutSelectingTheFirst()
    {
        using var original = Fixture();
        var x = original.SignalSeriesByIdentity.Keys.First(i => i.MessageName == "Joystick1" && i.SignalName == "pos_x");
        var other = x with { SignalName = "axisx" };
        var samples = original.DecodedSamples.Concat(new[] { new DecodedSignalSample(0, 0, -1, other, 0, BigInteger.Zero, "%") }).ToArray();
        using var dataset = new DatasetBuilder().Build(original.RawFrames, samples, [], new(0, 0, 0, 0, 0, ""));
        var profile = AnalysisProfileResolver.Recognize(dataset);
        var ambiguous = profile.Joysticks.Single(j => j.Name == "Joystick1");
        Assert.Null(ambiguous.X);
        Assert.Equal(2, Assert.Single(profile.MappingCandidates).Candidates.Count);
        var run = new AnalysisRunService().Analyze(dataset, new() { Profile = profile, Modules = AnalysisModules.Joystick });
        var result = run.Joysticks.Single(j => j.SubjectId == ambiguous.Id);
        Assert.Equal(ModuleStatus.AmbiguousMapping, result.Status);
        Assert.Contains("axisx", result.Explanation); Assert.Contains("pos_x", result.Explanation);
        Assert.Equal(4, result.Coverage.RequestedSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JoystickYIsDistinctFromYawAndRealDuplicateAxesRemainAmbiguous(bool duplicateY)
    {
        var x = new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "Joystick", "Joystick_Pos_X");
        var y = x with { SignalName = "Joystick_Pos_Y" };
        var yaw = x with { SignalName = "Joystick_Pos_Yaw" };
        var otherY = x with { SignalName = "Joystick_AxisY" };
        var identities = duplicateY ? new[] { x, y, yaw, otherY } : new[] { x, y, yaw };
        var samples = new List<DecodedSignalSample>();
        for (var index = 0; index <= 100; index++)
            foreach (var identity in identities)
                samples.Add(new(index * 10_000_000L, index, -1, identity, index, BigInteger.Zero, "%"));
        using var dataset = new DatasetBuilder().Build([Frame(0, "1"), Frame(1_000_000_000L, "1")], samples, [], new(0, 0, 0, 0, 0, ""));
        var profile = AnalysisProfileResolver.Recognize(dataset);
        var joystick = Assert.Single(profile.Joysticks);
        Assert.Equal(x, joystick.X);
        Assert.False(joystick.CalibrationConfirmed);
        var result = Assert.Single(new AnalysisRunService().Analyze(dataset,
            new() { Profile = profile, Modules = AnalysisModules.Joystick }).Joysticks);
        if (duplicateY)
        {
            Assert.Null(joystick.Y);
            var candidates = Assert.Single(profile.MappingCandidates);
            Assert.Equal("joystick.y", candidates.Role);
            Assert.Equal(2, candidates.Candidates.Count);
            Assert.Contains(y, candidates.Candidates);
            Assert.Contains(otherY, candidates.Candidates);
            Assert.DoesNotContain(yaw, candidates.Candidates);
            Assert.Equal(ModuleStatus.AmbiguousMapping, result.Status);
        }
        else
        {
            Assert.Equal(y, joystick.Y);
            Assert.Empty(profile.MappingCandidates);
            Assert.Equal(ModuleStatus.Partial, result.Status);
            Assert.NotNull(result.Value);
            Assert.False(result.Value!.IsCalibrated);
            Assert.True(result.Value.CoveredSeconds > 0);
        }
    }

    [Fact]
    public void ConfirmedProfileMustHaveMatchingSchemaAndSourceScope()
    {
        using var dataset = Fixture();
        var profile = AnalysisProfileResolver.Recognize(dataset) with { Confirmed = true };
        using var different = Fixture(source: new string('f', 64));
        Assert.Throws<ArgumentException>(() => new AnalysisRunService().Analyze(different, new() { Profile = profile }));
        Assert.Throws<ArgumentException>(() => new AnalysisRunService().Analyze(dataset, new() { Profile = profile with { SchemaVersion = 2 } }));
    }

    [Fact]
    public void DbcMigrationRetainsInvalidatedOptionalDependenciesInsteadOfDroppingTheirMeaning()
    {
        using var original = Fixture();
        original.SignalDefinitionHashes = original.SignalSeriesByIdentity.Keys.ToDictionary(i => i, _ => "original");
        var soc = original.SignalSeriesByIdentity.Keys.Single(i => i.SignalName == "SOC");
        var saved = AnalysisProfileResolver.Recognize(original);
        saved = saved with { Confirmed = true, Usage = new() { Activity = soc, Soc = soc, On = soc, ThresholdConfirmed = true },
            Actuators = saved.Actuators.Select(a => a with { RunSignal = soc, PositionReferenceConfirmed = true }).ToArray() };
        using var changed = Fixture(dbc: new string('c', 64));
        changed.SignalDefinitionHashes = changed.SignalSeriesByIdentity.Keys.ToDictionary(i => i, i => i == soc ? "changed-scale" : "original");
        var profile = AnalysisProfileResolver.Resolve(changed, [saved]);
        Assert.Equal(soc, profile.Usage!.On);
        Assert.All(profile.Actuators, a => Assert.Equal(soc, a.RunSignal));
        Assert.Contains(soc, profile.InvalidatedSignals);
        var run = new AnalysisRunService().Analyze(changed, new() { Profile = profile });
        Assert.Equal(ModuleStatus.MissingSignals, run.ActiveUsage.Status);
        Assert.Contains("missing_on_signal", run.ActiveUsage.ReasonCodes);
        Assert.All(run.Actuators, a => Assert.Null(a.Value!.Tracking.Value));
    }

    [Fact]
    public void ConfirmationCannotOverrideContradictoryDbcUnits()
    {
        using var original = Fixture();
        using var dataset = new DatasetBuilder().Build(original.RawFrames,
            original.DecodedSamples.Select(s => s.Identity.SignalName == "RealPos" ? s with { Unit = "counts" } : s).ToArray(),
            [], new(0, 0, 0, 0, 0, ""));
        var profile = AnalysisProfileResolver.Recognize(dataset);
        profile = profile with { Actuators = profile.Actuators.Select(a => a with { PositionReferenceConfirmed = true,
            Stroke = new() { CalibrationConfirmed = true, ManualCenter = 50, HalfRange = 50 } }).ToArray() };
        var result = new AnalysisRunService().Analyze(dataset, new() { Profile = profile, Modules = AnalysisModules.Actuators });
        Assert.All(result.Actuators, a =>
        {
            Assert.NotNull(a.Value!.SetpointChange.Value);
            Assert.Null(a.Value.PositionRequest.Value); Assert.Contains("unit_mismatch", a.Value.PositionRequest.ReasonCodes);
            Assert.Null(a.Value.Tracking.Value); Assert.Contains("unit_mismatch", a.Value.Tracking.ReasonCodes);
            Assert.NotNull(a.Value.Stroke!.Value);
            Assert.Equal(0, a.Value.Stroke.Value!.SetpointCoveredSeconds);
            Assert.Contains("unit_mismatch", a.Value.Stroke.ReasonCodes);
        });
    }

    private static RawCanFrame Frame(long ns, string channel, uint id = 1) => new(ns, id, 8, new byte[8], "Rx", channel, false);
    private static CanDataset Fixture(string? dbc = null, string? source = null)
    {
        var samples = new List<DecodedSignalSample>(); var frames = new List<RawCanFrame>();
        foreach (var channel in new[] { "1", "2" })
        {
            var joystick = new SignalIdentity(channel, CanFrameFormat.Classic, false, 1, "Joystick" + channel, "pos_x");
            var actuator = new SignalIdentity(channel, CanFrameFormat.Classic, false, 2, "ActuatorLeft" + channel, "RealPos");
            for (var i = 0; i <= 400; i++)
            {
                var ns = i * 10_000_000L;
                foreach (var (identity, value, unit) in new[] {
                    (joystick, i is >= 100 and < 120 ? 20d : 0, "%"), (joystick with { SignalName = "pos_y" }, 0d, "%"),
                    (actuator, i >= 100 ? 100d : 0, "mm"), (actuator with { SignalName = "ActualPos" }, i >= 115 ? 100d : 0, "mm") })
                    samples.Add(new(ns, samples.Count, -1, identity, value, BigInteger.Zero, unit));
                frames.Add(Frame(ns, channel)); frames.Add(Frame(ns, channel, 2));
            }
        }
        var soc = new SignalIdentity("1", CanFrameFormat.Classic, false, 3, "BMS", "SOC");
        for (var i = 0; i <= 400; i++) samples.Add(new(i * 10_000_000L, samples.Count, -1, soc, 80 - i * .001, BigInteger.Zero, "%"));
        return new DatasetBuilder().Build(frames.OrderBy(f => f.TimestampNanoseconds).ToArray(), samples.OrderBy(s => s.TimestampNanoseconds).ToArray(), [],
            new(0, 0, 0, 0, 0, ""), sourceLogSha256: source ?? new string('a', 64), dbcSha256: dbc ?? new string('b', 64));
    }
}
