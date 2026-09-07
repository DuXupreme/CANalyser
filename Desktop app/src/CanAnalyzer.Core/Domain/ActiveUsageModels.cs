namespace CanAnalyzer.Core.Domain;

public enum ActivityDetectionMethod { SignalThreshold, SocSlope }
public enum ActivitySignalDirection { Absolute, Positive, Negative }
public enum UsageState { Active, Idle, Off, UnknownActivity, Unknown }

public sealed record ActiveUsageOptions
{
    public ActivityDetectionMethod Method { get; init; }
    public ActivitySignalDirection Direction { get; init; }
    public double ActivityThreshold { get; init; } = 100;
    public double OnThreshold { get; init; } = 0.5;
    public double BridgePauseSeconds { get; init; } = 10;
    public double MinimumActivitySeconds { get; init; } = 3;
    public double MaximumSampleGapSeconds { get; init; } = 5;
    public double SocWindowSeconds { get; init; } = 60;
    public double SocDropPerHourThreshold { get; init; } = 5;
    public double BatteryCapacityKwh { get; init; } = 15;
    public double ReserveSocPercent { get; init; } = 20;
    public double? StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
}

public sealed record UsageInterval(double StartSeconds, double EndSeconds, UsageState State)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>Energy and its time denominator always have the same SOC coverage.</summary>
public sealed record UsageEnergy(double CoveredSeconds, double DischargeSocPoints, double RiseSocPoints, double CapacityKwh)
{
    public double NetSocPoints => DischargeSocPoints - RiseSocPoints;
    public double NetKwh => NetSocPoints * CapacityKwh / 100;
    public double? AverageKw => CoveredSeconds > 0 ? NetKwh * 3600 / CoveredSeconds : null;
}

public sealed record ActiveUsageResult
{
    public required IReadOnlyList<UsageInterval> Intervals { get; init; }
    public required IReadOnlyDictionary<UsageState, UsageEnergy> EnergyByState { get; init; }
    public required ActiveUsageOptions Options { get; init; }
    public bool HasOnSignal { get; init; }
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public double? StartSoc { get; init; }
    public double? EndSoc { get; init; }
    public double? StartSocTime { get; init; }
    public double? EndSocTime { get; init; }
    public double Duration(UsageState state) => Intervals.Where(x => x.State == state).Sum(x => x.DurationSeconds);
    public double ActiveSeconds => Duration(UsageState.Active);
    public double IdleSeconds => Duration(UsageState.Idle);
    public double OnSeconds => ActiveSeconds + IdleSeconds + Duration(UsageState.UnknownActivity);
    public double? ActivePercent => OnSeconds > 0 ? 100 * ActiveSeconds / OnSeconds : null;
    public UsageEnergy TotalEnergy => new(EnergyByState.Values.Sum(x => x.CoveredSeconds),
        EnergyByState.Values.Sum(x => x.DischargeSocPoints), EnergyByState.Values.Sum(x => x.RiseSocPoints), Options.BatteryCapacityKwh);

    // SOC rises can be charging or BMS corrections; neither is a sound discharge-only forecast.
    public bool CanProject => TotalEnergy.RiseSocPoints <= 0.5;
    public double? ActiveRuntimeHours => Runtime(EnergyByState[UsageState.Active]);
    public double? MixedRuntimeHours
    {
        get
        {
            if (OnSeconds <= 0 || Duration(UsageState.UnknownActivity) > 1e-6) return null;
            var active = EnergyByState[UsageState.Active];
            var idle = EnergyByState[UsageState.Idle];
            if (active.NetSocPoints + idle.NetSocPoints < 0.1) return null;
            if ((ActiveSeconds > 0 && active.CoveredSeconds < ActiveSeconds * 0.9) ||
                (IdleSeconds > 0 && idle.CoveredSeconds < IdleSeconds * 0.9)) return null;
            var kw = ((active.AverageKw ?? 0) * ActiveSeconds + (idle.AverageKw ?? 0) * IdleSeconds) / OnSeconds;
            return CanProject && kw > 0 ? Options.BatteryCapacityKwh / kw : null;
        }
    }
    public double? RemainingMixedHours => EndSocTime >= EndSeconds - Options.MaximumSampleGapSeconds && EndSoc.HasValue
        ? MixedRuntimeHours * Math.Max(0, EndSoc.Value - Options.ReserveSocPercent) / 100 : null;
    public double? RemainingActiveHours => EndSocTime >= EndSeconds - Options.MaximumSampleGapSeconds && EndSoc.HasValue
        ? ActiveRuntimeHours * Math.Max(0, EndSoc.Value - Options.ReserveSocPercent) / 100 : null;

    private double? Runtime(UsageEnergy energy) => CanProject && ActiveSeconds > 0 &&
        energy.CoveredSeconds >= ActiveSeconds * 0.9 && energy.NetSocPoints >= 0.1 && energy.AverageKw > 0
        ? Options.BatteryCapacityKwh / energy.AverageKw : null;
}
