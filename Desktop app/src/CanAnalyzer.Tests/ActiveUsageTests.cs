using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class ActiveUsageTests
{
    private readonly ActiveUsageAnalyzer _analyzer = new();
    private static ActiveUsageOptions Exact => new() { BridgePauseSeconds = 0, MinimumActivitySeconds = 0 };
    private static SignalSeries Series(string name, int end, Func<double, double> value, int step = 1)
    {
        var time = Enumerable.Range(0, end / step + 1).Select(i => (double)(i * step)).ToArray();
        return new SignalSeries(name, time, time.Select(value).ToArray());
    }

    [Fact]
    public void RpmAndSoc_UseElapsedTimeAndMatchedEnergyCoverage()
    {
        var rpm = Series("Motor.RPM", 600, t => t < 300 ? -1500 : 0);
        var soc = Series("SOC", 600, t => t <= 300 ? 100 - 8 * t / 300 : 92 - 2 * (t - 300) / 300, 2);
        var r = _analyzer.Analyze(rpm, soc, null, Exact);
        Assert.Equal(300, r.ActiveSeconds, 6);
        Assert.Equal(300, r.IdleSeconds, 6);
        Assert.Equal(50, r.ActivePercent!.Value, 6);
        Assert.Equal(1.5, r.TotalEnergy.NetKwh, 6);
        Assert.Equal(14.4, r.EnergyByState[UsageState.Active].AverageKw!.Value, 6);
        Assert.Equal(3.6, r.EnergyByState[UsageState.Idle].AverageKw!.Value, 6);
        Assert.Equal(15 / 14.4, r.ActiveRuntimeHours!.Value, 6);
        Assert.Equal(15 / 9d, r.MixedRuntimeHours!.Value, 6);
        Assert.Equal(15 / 9d * 0.7, r.RemainingMixedHours!.Value, 6);
        Assert.Equal(600, r.Intervals.Sum(i => i.DurationSeconds), 6);
    }

    [Fact]
    public void OnSignal_SeparatesOffAndUnknownActivityFromIdle()
    {
        var rpm = new SignalSeries("RPM", [0, 1, 2, 3, 4, 5, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20],
            Enumerable.Repeat(1000d, 17).ToArray());
        var on = Series("Ignition", 20, t => t < 15 ? 1 : 0);
        var r = _analyzer.Analyze(rpm, null, on, Exact with { MaximumSampleGapSeconds = 2 });
        Assert.True(r.HasOnSignal);
        Assert.Equal(15, r.OnSeconds, 6);
        Assert.Equal(10, r.ActiveSeconds, 6);
        Assert.Equal(5, r.Duration(UsageState.UnknownActivity), 6);
        Assert.Equal(5, r.Duration(UsageState.Off), 6);
        Assert.Equal(0, r.IdleSeconds, 6);
        Assert.Null(r.MixedRuntimeHours);
        Assert.Null(r.ActiveRuntimeHours);
    }

    [Fact]
    public void OverlappingImportGaps_AreUnknownAndNeverSupplySocEnergy()
    {
        var rpm = Series("RPM", 300, _ => 1000);
        var soc = Series("SOC", 300, t => 100 - t / 10);
        var r = _analyzer.Analyze(rpm, soc, null, Exact with { BridgePauseSeconds = 1000 },
            [new MeasurementGap(190, 250), new MeasurementGap(100, 200)]);
        Assert.Equal(150, r.ActiveSeconds, 6);
        Assert.Equal(150, r.Duration(UsageState.Unknown), 6);
        Assert.Equal(150, r.TotalEnergy.CoveredSeconds, 6);
        Assert.Equal(15, r.TotalEnergy.NetSocPoints, 6);
        Assert.Equal(30, (r.StartSoc - r.EndSoc)!.Value, 6);
        Assert.Equal(2, r.Intervals.Count(i => i.State == UsageState.Active));
    }

    [Fact]
    public void SampleOutages_AreNotBridgedEvenWhenPauseSettingIsLong()
    {
        var time = Enumerable.Range(0, 11).Concat(Enumerable.Range(100, 11)).Select(i => (double)i).ToArray();
        var rpm = new SignalSeries("RPM", time, time.Select(_ => 1000d).ToArray());
        var r = _analyzer.Analyze(rpm, null, null, Exact with { BridgePauseSeconds = 1000 });
        Assert.Equal(20, r.ActiveSeconds, 6);
        Assert.Equal(90, r.Duration(UsageState.Unknown), 6);
    }

    [Fact]
    public void KnownShortPausesAreMerged_AndIsolatedSpikesAreRejected()
    {
        var rpm = Series("RPM", 40, t => t is >= 2 and < 8 or >= 10 and < 15 or >= 30 and < 31 ? 1000 : 0);
        var r = _analyzer.Analyze(rpm, null, null, Exact with { BridgePauseSeconds = 3, MinimumActivitySeconds = 3 });
        var active = Assert.Single(r.Intervals, i => i.State == UsageState.Active);
        Assert.Equal(2, active.StartSeconds);
        Assert.Equal(15, active.EndSeconds);
        Assert.Equal(13, r.ActiveSeconds, 6);
    }

    [Fact]
    public void SparseSoc_DoesNotDilutePowerAcrossMissingSocTime_OrAllowForecast()
    {
        var rpm = Series("RPM", 200, _ => 1000);
        var time = Enumerable.Range(0, 101).Concat(Enumerable.Range(190, 11)).Select(i => (double)i).ToArray();
        var soc = new SignalSeries("SOC", time, time.Select(t => 100 - t / 100).ToArray());
        var r = _analyzer.Analyze(rpm, soc, null, Exact);
        Assert.Equal(200, r.ActiveSeconds, 6);
        Assert.Equal(110, r.EnergyByState[UsageState.Active].CoveredSeconds, 6);
        Assert.Equal(5.4, r.EnergyByState[UsageState.Active].AverageKw!.Value, 6);
        Assert.Null(r.ActiveRuntimeHours);
        Assert.Null(r.MixedRuntimeHours);
    }

    [Fact]
    public void SocWindows_DetectSteepStaircaseDeclinesInsteadOfIndividualSteps()
    {
        var soc = Series("SOC", 300, t => 100 - Math.Clamp(Math.Floor((t - 120) / 60), 0, 2));
        var r = _analyzer.Analyze(null, soc, null, Exact with
        {
            Method = ActivityDetectionMethod.SocSlope, SocWindowSeconds = 60, SocDropPerHourThreshold = 30
        });
        Assert.Equal(120, r.ActiveSeconds, 6);
        Assert.Equal(180, r.IdleSeconds, 6);
        Assert.Equal(2, r.EnergyByState[UsageState.Active].NetSocPoints, 6);
    }

    [Fact]
    public void RangeClipping_InterpolatesSocOnlyBetweenValidBracketingSamples()
    {
        var rpm = Series("RPM", 100, _ => 1000, 2);
        var soc = Series("SOC", 100, t => 100 - t / 10, 2);
        var r = _analyzer.Analyze(rpm, soc, null, Exact with { StartSeconds = 10.5, EndSeconds = 20.5 });
        Assert.Equal(10, r.ActiveSeconds, 6);
        Assert.Equal(98.95, r.StartSoc!.Value, 6);
        Assert.Equal(97.95, r.EndSoc!.Value, 6);
        Assert.Equal(0.15, r.TotalEnergy.NetKwh, 6);
    }

    [Fact]
    public void ChargingOrSocCorrection_IsReportedAndSuppressesRuntimeForecasts()
    {
        var rpm = Series("RPM", 100, _ => 1000);
        var soc = Series("SOC", 100, t => t <= 50 ? 90 - t / 10 : 85 + (t - 50) / 50);
        var r = _analyzer.Analyze(rpm, soc, null, Exact);
        Assert.Equal(5, r.TotalEnergy.DischargeSocPoints, 6);
        Assert.Equal(1, r.TotalEnergy.RiseSocPoints, 6);
        Assert.Equal(4, r.TotalEnergy.NetSocPoints, 6);
        Assert.False(r.CanProject);
        Assert.Null(r.ActiveRuntimeHours);
        Assert.Null(r.MixedRuntimeHours);
    }

    [Fact]
    public void InvalidSocAndStaleEndpoint_DoNotProduceRemainingBatteryLife()
    {
        var rpm = Series("RPM", 100, _ => 1000);
        var soc = Series("SOC", 100, t => t > 80 ? 255 : 100 - t / 10);
        var r = _analyzer.Analyze(rpm, soc, null, Exact);
        Assert.Equal(80, r.TotalEnergy.CoveredSeconds, 6);
        Assert.Equal(92, r.EndSoc);
        Assert.Null(r.RemainingActiveHours);
        Assert.Null(r.RemainingMixedHours);
    }

    [Fact]
    public void ZeroDischarge_DoesNotPredictInfiniteRuntime()
    {
        var r = _analyzer.Analyze(Series("RPM", 100, _ => 1000), Series("SOC", 100, _ => 80), null, Exact);
        Assert.Equal(0, r.TotalEnergy.NetKwh);
        Assert.Null(r.ActiveRuntimeHours);
        Assert.Null(r.MixedRuntimeHours);
    }

    [Fact]
    public void BmsCurrent_CanDistinguishNegativeDischargeFromPositiveCharging()
    {
        var current = Series("BMS.Current", 60, t => t < 20 ? -12 : t < 40 ? 12 : 2);
        var negative = _analyzer.Analyze(current, null, null, Exact with { ActivityThreshold = 5, Direction = ActivitySignalDirection.Negative });
        var positive = _analyzer.Analyze(current, null, null, Exact with { ActivityThreshold = 5, Direction = ActivitySignalDirection.Positive });
        var both = _analyzer.Analyze(current, null, null, Exact with { ActivityThreshold = 5 });
        Assert.Equal(20, negative.ActiveSeconds);
        Assert.Equal(0, Assert.Single(negative.Intervals, i => i.State == UsageState.Active).StartSeconds);
        Assert.Equal(20, Assert.Single(positive.Intervals, i => i.State == UsageState.Active).StartSeconds);
        Assert.Equal(40, both.ActiveSeconds);
    }

    [Fact]
    public void InvalidInputsFailClearly_AndCancellationIsRespected()
    {
        var rpm = Series("RPM", 100, _ => 1000);
        Assert.Throws<ArgumentException>(() => _analyzer.Analyze(rpm, null, null, Exact with { BatteryCapacityKwh = double.NaN }));
        Assert.Throws<ArgumentException>(() => _analyzer.Analyze(rpm, null, null, Exact with { StartSeconds = 20, EndSeconds = 10 }));
        Assert.Throws<ArgumentException>(() => _analyzer.Analyze(new SignalSeries("RPM", [0, 1, 1, 2], [0, 100, 200, 200]), null, null, Exact));
        Assert.Throws<OperationCanceledException>(() => _analyzer.Analyze(rpm, null, null, Exact, cancellationToken: new CancellationToken(true)));
    }
}
