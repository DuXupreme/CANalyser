using CanAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class ActiveUsageViewModel
{
    private CancellationTokenSource? _pendingAutomatic;
    private SignalIdentity? _unavailableOn;
    private string? _unavailableOnLabel;
    [ObservableProperty] private bool _activityThresholdConfirmed;
    [ObservableProperty] private bool _capacityConfirmed;

    public void ApplyProfile(UsageAnalysisProfile? profile, BatteryAnalysisProfile? battery, IReadOnlyList<SignalIdentity>? invalidatedSignals = null)
    {
        if (_dataset is null) return;
        _loading = true;
        try
        {
            ActivityThresholdConfirmed = profile?.ThresholdConfirmed == true;
            CapacityConfirmed = profile?.CapacityConfirmed == true;
            if (profile is not null)
            {
                ActivitySignal = Label(profile.Activity); SocSignal = Label(profile.Soc); OnSignal = Label(profile.On) ?? NoSignal;
                _unavailableOn = profile.On is not null && (Label(profile.On) is null || invalidatedSignals?.Contains(profile.On) == true) ? profile.On : null;
                _unavailableOnLabel = _unavailableOn?.DisplayLabel + " (ontbreekt of gewijzigd; kies opnieuw)";
                if (_unavailableOn is not null)
                {
                    if (!OnSignals.Contains(_unavailableOnLabel)) OnSignals.Add(_unavailableOnLabel);
                    OnSignal = _unavailableOnLabel;
                }
                var o = profile.Options;
                DirectionIndex = (int)o.Direction; UseSocSlope = o.Method == ActivityDetectionMethod.SocSlope;
                Threshold = Number(o.ActivityThreshold); OnThreshold = Number(o.OnThreshold); BridgeSeconds = Number(o.BridgePauseSeconds);
                MinimumSeconds = Number(o.MinimumActivitySeconds); MaxGapSeconds = Number(o.MaximumSampleGapSeconds);
                SocWindow = Number(o.SocWindowSeconds); SocRateThreshold = Number(o.SocDropPerHourThreshold);
                CapacityKwh = Number(o.BatteryCapacityKwh); ReserveSoc = Number(o.ReserveSocPercent);
            }
            else { ActivitySignal = SocSignal = null; OnSignal = NoSignal; UseSocSlope = false; _unavailableOn = null; _unavailableOnLabel = null; }
            BatteryCountSignal = Label(battery?.ActivePackCount);
            ConfirmedBatteryCount = battery?.ConfirmedCount?.ToString() ?? string.Empty;
            BatteryCountSourceIndex = BatteryCountSignal is not null ? 1 : battery?.ConfirmedCount is not null ? 2 : 0;
        }
        finally { _loading = false; }
        Invalidate();
    }

    private string? Label(SignalIdentity? identity) => identity is not null && _dataset?.SignalSeriesByIdentity.TryGetValue(identity, out var signal) == true ? signal.Label : null;
    private SignalIdentity? Identity(string? label) => label is not null && _dataset?.SignalSeriesByLabel.TryGetValue(label, out var signal) == true ? signal.Identity : null;

    public UsageAnalysisProfile? CaptureProfile()
    {
        if (_dataset is null || Identity(ActivitySignal) is null && Identity(SocSignal) is null) return null;
        try
        {
            return new UsageAnalysisProfile
            {
                Activity = Identity(ActivitySignal), Soc = Identity(SocSignal), On = Identity(OnSignal) ?? (OnSignal == _unavailableOnLabel ? _unavailableOn : null),
                ThresholdConfirmed = ActivityThresholdConfirmed, CapacityConfirmed = CapacityConfirmed,
                Options = new ActiveUsageOptions
                {
                    Method = UseSocSlope ? ActivityDetectionMethod.SocSlope : ActivityDetectionMethod.SignalThreshold,
                    Direction = (ActivitySignalDirection)DirectionIndex,
                    ActivityThreshold = Parse(Threshold, "Activiteitsgrens"), OnThreshold = Parse(OnThreshold, "Aan-grens"),
                    BridgePauseSeconds = Parse(BridgeSeconds, "Korte pauze"), MinimumActivitySeconds = Parse(MinimumSeconds, "Minimum periode"),
                    MaximumSampleGapSeconds = Parse(MaxGapSeconds, "Meetafstand"), SocWindowSeconds = Parse(SocWindow, "SOC-venster"),
                    SocDropPerHourThreshold = Parse(SocRateThreshold, "SOC-daling"), BatteryCapacityKwh = Parse(CapacityKwh, "Capaciteit"),
                    ReserveSocPercent = Parse(ReserveSoc, "Reserve")
                }
            };
        }
        catch (ArgumentException) { return null; }
    }

    public BatteryAnalysisProfile? CaptureBatteryProfile() => UsesBatteryCountSignal
        ? new(Identity(BatteryCountSignal), null)
        : UsesManualBatteryCount && int.TryParse(ConfirmedBatteryCount, out var count) && count > 0 ? new(null, count) : null;

    private void ScheduleAutomaticUsage()
    {
        if (_loading || _dataset is null) return;
        _pendingAutomatic?.Cancel();
        var pending = new CancellationTokenSource();
        _pendingAutomatic = pending;
        _ = CalculateScheduledAsync(pending);
    }

    private async Task CalculateScheduledAsync(CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(400, pending.Token);
            while (IsBusy) await Task.Delay(50, pending.Token);
            if (!pending.IsCancellationRequested && _dataset is not null) await CalculateAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_pendingAutomatic, pending)) _pendingAutomatic = null;
            pending.Dispose();
        }
    }

    public void ClearDataset()
    {
        _pendingAutomatic?.Cancel(); _calculation?.Cancel();
        _dataset = null; Invalidate();
        Signals.Clear(); OnSignals.Clear(); Timeline = new();
        Status = "Geen dataset geladen.";
        CalculateCommand.NotifyCanExecuteChanged();
    }

    public async Task CancelAndWaitAsync()
    {
        _pendingAutomatic?.Cancel(); _calculation?.Cancel(); _revision++;
        while (IsBusy) await Task.Delay(25);
    }
}
