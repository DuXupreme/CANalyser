using System.Collections.ObjectModel;
using System.ComponentModel;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanAnalyzer.App.ViewModels;

public sealed record AnalysisOverviewCard(string Title, string Answer, string Basis, string State, int DetailTabIndex, string? SubjectId = null);

public sealed partial class JoystickAnalyticsViewModel
{
    private IReadOnlyList<AnalysisProfile> _savedProfiles = [];
    private CancellationTokenSource? _scheduledAnalysis;
    private CancellationTokenSource? _analysisCalculation;
    private StandardAnalysisRun? _standardRun;
    private bool _profileObserversAttached;
    private int _profileSelectionDepth;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileChangeStatus))]
    private bool _hasUnsavedProfileChanges;
    [ObservableProperty] private AnalysisProfile? _currentProfile;
    [ObservableProperty] private string _profileName = "Mijn analyseprofiel";
    [ObservableProperty] private string _profileStatus = "Een bevestigd profiel bewaart signaalkoppelingen en bekende grenzen.";
    [ObservableProperty] private bool _positionReferenceConfirmed;
    [ObservableProperty] private bool _canBitratesConfirmed;
    [ObservableProperty] private int _selectedAnalysisTabIndex;
    [ObservableProperty] private string _overviewStatus = "Laad een dataset. Beschikbare analyses worden automatisch berekend.";
    [ObservableProperty] private JoystickAnalysisProfile? _selectedJoystickProfile;
    public ObservableCollection<JoystickAnalysisProfile> AvailableJoystickProfiles { get; } = [];
    public ObservableCollection<AnalysisOverviewCard> OverviewCards { get; } = [];
    public event Action<AnalysisProfile>? ProfileConfirmed;

    public string ProfileChangeStatus => CurrentProfile?.Confirmed == true
        ? $"Detailinstellingen gewijzigd sinds de laatste profielbevestiging. Overzicht en machine-export gebruiken nog profiel ‘{CurrentProfile.Name}’, versie {CurrentProfile.Version}. Bevestig en bewaar om de wijzigingen daarvoor te gebruiken."
        : "Detailinstellingen gewijzigd. Overzicht en machine-export gebruiken nog automatische herkenning. Bevestig en bewaar een profiel om de detailinstellingen daarvoor te gebruiken.";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(ProfileName) or nameof(SelectedJoystickXSignal) or nameof(SelectedJoystickYSignal) or
            nameof(UseJoystickCalibration) or nameof(JoystickXMinimum) or nameof(JoystickXNeutral) or nameof(JoystickXMaximum) or
            nameof(JoystickYMinimum) or nameof(JoystickYNeutral) or nameof(JoystickYMaximum) or nameof(DeadzoneThreshold) or
            nameof(SaturationThreshold) or nameof(HistogramBins) or nameof(UsageMaximumGapSeconds) or nameof(StrokeEdgePercent) or
            nameof(SelectedCommandSignal) or nameof(SelectedResponseSignal) or nameof(SelectedActuatorLeftSignal) or
            nameof(SelectedActuatorRightSignal) or nameof(SelectedActuatorFrontSignal) or nameof(PositionReferenceConfirmed) or
            nameof(DelaySearchRangeSeconds) or nameof(UseAutomaticResponseThreshold) or nameof(ResponseThresholdAbsolute) or
            nameof(UseAutomaticPositionTolerance) or nameof(PositionToleranceAbsolute) or nameof(CanBitrateKbps) or
            nameof(CanDataBitrateKbps) or nameof(CanBitratesConfirmed))
            MarkProfileSettingsChanged();
    }

    private void MarkProfileSettingsChanged()
    {
        if (!_loadingDataset && _profileSelectionDepth == 0 && _dataset is not null && CurrentProfile is not null)
            HasUnsavedProfileChanges = true;
    }

    private void AttachProfileObservers()
    {
        if (_profileObserversAttached) return;
        _profileObserversAttached = true;
        foreach (var range in ActuatorRanges) range.PropertyChanged += (_, _) => MarkProfileSettingsChanged();
        ActiveUsage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ActiveUsageViewModel.ActivitySignal) or nameof(ActiveUsageViewModel.SocSignal) or
                nameof(ActiveUsageViewModel.OnSignal) or nameof(ActiveUsageViewModel.DirectionIndex) or nameof(ActiveUsageViewModel.UseSocSlope) or
                nameof(ActiveUsageViewModel.Threshold) or nameof(ActiveUsageViewModel.OnThreshold) or nameof(ActiveUsageViewModel.BridgeSeconds) or
                nameof(ActiveUsageViewModel.MinimumSeconds) or nameof(ActiveUsageViewModel.MaxGapSeconds) or nameof(ActiveUsageViewModel.SocWindow) or
                nameof(ActiveUsageViewModel.SocRateThreshold) or nameof(ActiveUsageViewModel.CapacityKwh) or nameof(ActiveUsageViewModel.ReserveSoc) or
                nameof(ActiveUsageViewModel.BatteryCountSourceIndex) or nameof(ActiveUsageViewModel.BatteryCountSignal) or
                nameof(ActiveUsageViewModel.ConfirmedBatteryCount) or nameof(ActiveUsageViewModel.ActivityThresholdConfirmed) or
                nameof(ActiveUsageViewModel.CapacityConfirmed)) MarkProfileSettingsChanged();
        };
    }

    public void ConfigureProfiles(IEnumerable<AnalysisProfile> profiles) => _savedProfiles = profiles.ToArray();

    partial void OnPositionReferenceConfirmedChanged(bool value)
    {
        InvalidateUsage(); InvalidateTracking(); InvalidateDelay();
    }

    partial void OnCanBitratesConfirmedChanged(bool value)
    {
        _canDirty = true; _analysisRevision++; ScheduleAutomaticAnalysis();
    }

    private void ApplyDatasetProfile(CanDataset dataset)
    {
        AttachProfileObservers();
        _standardRun = null;
        OverviewCards.Clear();
        OverviewStatus = "Signalen herkend; antwoorden worden automatisch voorbereid…";
        CurrentProfile = AnalysisProfileResolver.Resolve(dataset, _savedProfiles);
        OverviewCards.Add(new("Hoe wordt de joystick bediend?", "Bedieningsverdeling en korte correcties worden voorbereid.", "Volledige dataset; bekende grenzen worden uit het profiel gelezen.", "Berekenen", 2));
        OverviewCards.Add(new("Hoe reageren en volgen de actuatoren?", "Reactietijd, volgfout en slagbenutting worden voorbereid.", "Elke herkende actuator behoudt eigen meetbasis en configuratiestatus.", "Berekenen", 1));
        OverviewCards.Add(new("Hoe lang is de machine actief?", "Actieve, inactieve en onbekende tijd worden voorbereid.", "De activiteitsdefinitie wordt bij het resultaat vermeld.", "Berekenen", 4));
        OverviewCards.Add(new("Wat weten we over de accuconfiguratie?", "Expliciete accuaantalbron en wijzigingen worden gecontroleerd.", "Een onbekend aantal blijft onbekend.", "Berekenen", 4));
        OverviewCards.Add(new("Is de meetdata bruikbaar?", "CAN-verkeer, meetgaten en loggerbewijs worden gecontroleerd.", "Interne loggerdiagnostiek blijft gescheiden van machine-CAN.", "Berekenen", 3));
        ProfileName = CurrentProfile.Confirmed ? CurrentProfile.Name : "Mijn analyseprofiel";
        ProfileStatus = CurrentProfile.Confirmed
            ? $"Bevestigd profiel: {CurrentProfile.Name} (versie {CurrentProfile.Version})."
            : _savedProfiles.Any(p => p.Confirmed)
                ? "Geen passend bevestigd profiel. Een andere DBC of machine vereist controle van de koppelingen."
                : "Signalen automatisch herkend. Bevestig bekende grenzen om fysieke uitslag te berekenen.";
        if (CurrentProfile.InvalidatedSignals.Count > 0) ProfileStatus = CurrentProfile.Evidence;
        AvailableJoystickProfiles.Clear();
        foreach (var joystick in CurrentProfile.Joysticks) AvailableJoystickProfiles.Add(joystick);
        SelectedJoystickProfile = AvailableJoystickProfiles.FirstOrDefault();
        if (SelectedJoystickProfile is { } selected) ApplyJoystickProfile(selected);
        foreach (var actuator in CurrentProfile.Actuators)
            ApplyActuatorRangeProfile(actuator);
        var pair = CurrentProfile.Actuators.FirstOrDefault(a => a.Command is not null && a.Feedback is not null);
        if (pair is not null) SelectReactionPairFromOverview(pair);
        else AutoDetectDelaySignals();
        var selectedActuator = CurrentProfile.Actuators.FirstOrDefault(a => Label(a.Command) == SelectedCommandSignal && Label(a.Feedback) == SelectedResponseSignal);
        PositionReferenceConfirmed = selectedActuator?.PositionReferenceConfirmed == true;
        // Initial navigation must open a usable reference without confirming unknown calibration.
        // Later explicit user selections are left alone by recomputation and profile editing.
        SelectedReactionReference = ReactionReferences[PositionReferenceConfirmed ? 1 : 0];
        var response = selectedActuator?.ResponseOptions ?? new MotionResponseOptions();
        DelaySearchRangeSeconds = response.MaxDelaySeconds;
        UseAutomaticResponseThreshold = !response.ResponseThresholdAbsolute.HasValue;
        ResponseThresholdAbsolute = response.ResponseThresholdAbsolute ?? .1;
        UseAutomaticPositionTolerance = !response.PositionToleranceAbsolute.HasValue;
        PositionToleranceAbsolute = response.PositionToleranceAbsolute ?? .1;
        UsageMaximumGapSeconds = CurrentProfile.MaximumGapSeconds;
        CanBitrateKbps = CurrentProfile.CanBitrateKbps; CanDataBitrateKbps = CurrentProfile.CanDataBitrateKbps;
        CanBitratesConfirmed = CurrentProfile.CanBitratesConfirmed;
        ActiveUsage.ApplyProfile(CurrentProfile.Usage, CurrentProfile.Battery, CurrentProfile.InvalidatedSignals);
        HasUnsavedProfileChanges = false;
    }

    private void ApplyActuatorRangeProfile(ActuatorAnalysisProfile actuator)
    {
        var name = actuator.Name.ToLowerInvariant();
        var index = name.Contains("left") || name.Contains("links") ? 0 : name.Contains("right") || name.Contains("rechts") ? 1
            : name.Contains("front") || name.Contains("voor") ? 2 : -1;
        if (index < 0) return;
        var range = ActuatorRanges[index];
        range.SetpointSignal = Label(actuator.Command); range.RunSignal = Label(actuator.RunSignal) ?? actuator.RunSignal?.DisplayLabel; range.RunValue = actuator.RunValue;
        range.ManualCenter = actuator.Stroke?.CalibrationConfirmed == true ? actuator.Stroke.ManualCenter : null;
        range.HalfRange = actuator.Stroke?.CalibrationConfirmed == true ? actuator.Stroke.HalfRange : null;
        range.CenterSignal = actuator.Stroke?.CalibrationConfirmed == true ? Label(actuator.Stroke.CenterSignal) ?? actuator.Stroke.CenterSignal?.DisplayLabel : null;
        range.HalfRangeSignal = actuator.Stroke?.CalibrationConfirmed == true ? Label(actuator.Stroke.HalfRangeSignal) ?? actuator.Stroke.HalfRangeSignal?.DisplayLabel : null;
        if (actuator.Stroke is { } stroke) StrokeEdgePercent = stroke.EdgePercent;
        var feedback = Label(actuator.Feedback);
        if (index == 0) SelectedActuatorLeftSignal = feedback;
        else if (index == 1) SelectedActuatorRightSignal = feedback;
        else SelectedActuatorFrontSignal = feedback;
    }

    private string? Label(SignalIdentity? id) => id is not null && _dataset?.SignalSeriesByIdentity.TryGetValue(id, out var signal) == true ? signal.Label : null;
    private SignalIdentity? Identity(string? label) => TryGetSeries(label, out var series) ? series.Identity : null;

    private void RefreshPositionReferenceConfirmation()
    {
        PositionReferenceConfirmed = CurrentProfile?.Actuators.Any(a => a.PositionReferenceConfirmed &&
            a.Command == Identity(SelectedCommandSignal) && a.Feedback == Identity(SelectedResponseSignal)) == true;
    }

    partial void OnSelectedJoystickProfileChanged(JoystickAnalysisProfile? value)
    {
        if (value is not null) ApplyJoystickProfile(value);
    }

    private void ApplyJoystickProfile(JoystickAnalysisProfile profile)
    {
        _profileSelectionDepth++;
        try
        {
        SelectedJoystickXSignal = Label(profile.X); SelectedJoystickYSignal = Label(profile.Y);
        UseJoystickCalibration = false;
        if (profile.CalibrationConfirmed && profile.XCalibration is { } x && profile.YCalibration is { } y)
        {
            JoystickXMinimum = x.Minimum; JoystickXNeutral = x.Neutral; JoystickXMaximum = x.Maximum;
            JoystickYMinimum = y.Minimum; JoystickYNeutral = y.Neutral; JoystickYMaximum = y.Maximum;
            UseJoystickCalibration = true;
        }
        DeadzoneThreshold = profile.NeutralThreshold; SaturationThreshold = profile.FullThreshold; HistogramBins = profile.HistogramBins;
        }
        finally { _profileSelectionDepth--; }
    }

    [RelayCommand]
    private void ConfirmProfile()
    {
        if (_dataset is null || CurrentProfile is null) return;
        if (UseJoystickCalibration && !HasValidJoystickAxisCalibration)
        { ProfileStatus = "Controleer eerst minimum < neutraal < maximum voor beide assen."; return; }
        var joysticks = CurrentProfile.Joysticks.ToList();
        if (Identity(SelectedJoystickXSignal) is { } x && Identity(SelectedJoystickYSignal) is { } y)
        {
            var old = joysticks.FirstOrDefault(p => p.Id == SelectedJoystickProfile?.Id || p.X == x && p.Y == y);
            var updated = new JoystickAnalysisProfile(old?.Id ?? "manual-joystick", old?.Name ?? "Gekozen joystick", x, y)
            {
                CalibrationConfirmed = UseJoystickCalibration,
                XCalibration = UseJoystickCalibration ? CreateJoystickXCalibration() : null,
                YCalibration = UseJoystickCalibration ? CreateJoystickYCalibration() : null,
                NeutralThreshold = DeadzoneThreshold, FullThreshold = SaturationThreshold, HistogramBins = HistogramBins
            };
            if (old is not null) joysticks.Remove(old);
            joysticks.Add(updated);
        }
        var actuators = CurrentProfile.Actuators.ToList();
        if (Identity(SelectedCommandSignal) is { } command && Identity(SelectedResponseSignal) is { } feedback)
        {
            var old = actuators.FirstOrDefault(p => p.Command == command || p.Feedback == feedback);
            var updated = new ActuatorAnalysisProfile(old?.Id ?? "manual-actuator", old?.Name ?? SelectedReactionActuator, command, feedback)
            {
                PositionReferenceConfirmed = PositionReferenceConfirmed,
                RunSignal = old?.RunSignal, RunValue = old?.RunValue ?? 1,
                Stroke = old?.Stroke,
                ResponseOptions = new MotionResponseOptions { MaxDelaySeconds = DelaySearchRangeSeconds,
                    ResponseThresholdAbsolute = UseAutomaticResponseThreshold ? null : ResponseThresholdAbsolute,
                    PositionToleranceAbsolute = UseAutomaticPositionTolerance ? null : PositionToleranceAbsolute }
            };
            if (old is not null) actuators.Remove(old);
            actuators.Add(updated);
        }
        var feedbackLabels = new[] { SelectedActuatorLeftSignal, SelectedActuatorRightSignal, SelectedActuatorFrontSignal };
        for (var i = 0; i < ActuatorRanges.Count; i++)
        {
            if (Identity(feedbackLabels[i]) is not { } rangeFeedback) continue;
            var range = ActuatorRanges[i];
            if (range.ManualCenter is { } centerValue && !double.IsFinite(centerValue) ||
                range.HalfRange is { } halfValue && (!double.IsFinite(halfValue) || halfValue <= 0) || !double.IsFinite(range.RunValue) ||
                !double.IsFinite(StrokeEdgePercent) || StrokeEdgePercent is <= 0 or >= 50)
            { ProfileStatus = "Controleer het bekende nulpunt, positieve halfbereik, bedrijfswaarde en grensband van " + range.Name + "."; return; }
            var rangeCommand = Identity(range.SetpointSignal);
            var old = actuators.FirstOrDefault(a => a.Feedback == rangeFeedback || rangeCommand is not null && a.Command == rangeCommand);
            var center = !string.IsNullOrWhiteSpace(range.CenterSignal) ? Identity(range.CenterSignal) ?? old?.Stroke?.CenterSignal : null;
            var half = !string.IsNullOrWhiteSpace(range.HalfRangeSignal) ? Identity(range.HalfRangeSignal) ?? old?.Stroke?.HalfRangeSignal : null;
            var validReference = (center is null ? range.ManualCenter.HasValue : _dataset.SignalSeriesByIdentity.ContainsKey(center)) &&
                (half is null ? range.HalfRange is > 0 : _dataset.SignalSeriesByIdentity.ContainsKey(half));
            var updated = (old ?? new ActuatorAnalysisProfile("manual:" + rangeFeedback.DisplayLabel, range.Name, rangeCommand, rangeFeedback)) with
            {
                Command = rangeCommand, Feedback = rangeFeedback,
                RunSignal = !string.IsNullOrWhiteSpace(range.RunSignal) ? Identity(range.RunSignal) ?? old?.RunSignal : null, RunValue = range.RunValue,
                Stroke = new() { ManualCenter = range.ManualCenter, HalfRange = range.HalfRange, CenterSignal = center,
                    HalfRangeSignal = half, EdgePercent = StrokeEdgePercent, HistogramBins = HistogramBins, CalibrationConfirmed = validReference }
            };
            if (old is not null) actuators.Remove(old);
            actuators.Add(updated);
        }
        var profile = CurrentProfile with
        {
            Id = CurrentProfile.Confirmed ? CurrentProfile.Id : Guid.NewGuid().ToString("N"),
            Version = CurrentProfile.Confirmed ? CurrentProfile.Version + 1 : 1,
            Name = string.IsNullOrWhiteSpace(ProfileName) ? "Mijn analyseprofiel" : ProfileName.Trim(), Confirmed = true,
            Evidence = "Door gebruiker bevestigd via analyseprofiel; kalibratie en referentie hebben eigen bevestigingsstatus.",
            Joysticks = joysticks, Actuators = actuators, MaximumGapSeconds = UsageMaximumGapSeconds,
            CanBitrateKbps = CanBitrateKbps, CanDataBitrateKbps = CanDataBitrateKbps, CanBitratesConfirmed = CanBitratesConfirmed,
            ConfirmedDecoderChannels = _dataset.Channels.Order(StringComparer.Ordinal).ToArray(),
            InvalidatedSignals = CurrentProfile.InvalidatedSignals.Where(id => !_dataset.SignalSeriesByIdentity.ContainsKey(id)).ToArray(),
            Usage = ActiveUsage.CaptureProfile(), Battery = ActiveUsage.CaptureBatteryProfile()
        };
        _savedProfiles = _savedProfiles.Where(p => p.Id != profile.Id).Append(profile).ToArray();
        CurrentProfile = profile; _standardRun = null;
        _detailCache.Clear(); _usageDirty = _trackingDirty = _reactionDirty = _canDirty = true;
        ProfileStatus = $"Profiel {profile.Name} bevestigd. Wordt hergebruikt bij dezelfde DBC en machine/installatie; zonder bekende machine uitsluitend deze bronlog.";
        HasUnsavedProfileChanges = false;
        ProfileConfirmed?.Invoke(profile);
        ScheduleAutomaticAnalysis();
    }

    [RelayCommand]
    private void InspectAnalysis(AnalysisOverviewCard? card)
    {
        if (card is null) return;
        if (card.DetailTabIndex == 2 && AvailableJoystickProfiles.FirstOrDefault(j => j.Id == card.SubjectId) is { } joystick)
            SelectedJoystickProfile = joystick;
        if (card.DetailTabIndex == 1 && CurrentProfile?.Actuators.FirstOrDefault(a => a.Id == card.SubjectId) is { } actuator)
            SelectReactionPairFromOverview(actuator);
        SelectedAnalysisTabIndex = card.DetailTabIndex;
    }

    private void ScheduleAutomaticAnalysis()
    {
        if (_loadingDataset || _dataset is null) return;
        _analysisCalculation?.Cancel();
        _scheduledAnalysis?.Cancel();
        var pending = new CancellationTokenSource();
        _scheduledAnalysis = pending;
        _ = RunScheduledAsync(pending);
    }

    private async Task RunScheduledAsync(CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(350, pending.Token);
            if (!pending.IsCancellationRequested && _dataset is not null) await RecomputeAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_scheduledAnalysis, pending)) _scheduledAnalysis = null;
            pending.Dispose();
        }
    }

    private async Task BuildOverviewAsync()
    {
        if (_standardRun is null && _dataset is { } dataset)
        {
            var request = new AnalysisRunRequest { Profile = CurrentProfile };
            var cancellation = _analysisCalculation?.Token ?? default;
            _standardRun = await ComputeAsync(() => new AnalysisRunService().Analyze(dataset, request, cancellation));
        }
        if (_standardRun is not { } run) return;
        OverviewCards.Clear();
        foreach (var joystick in run.Joysticks)
        {
            var result = joystick.Value;
            var answer = result is { IsCalibrated: true }
                ? $"{result.InputEpisodes.Count(e => e.IsShortCorrection):N0} korte correcties; {result.FullHolds.Count:N0} perioden voluit ≥ 2 s."
                : joystick.Explanation;
            OverviewCards.Add(new("Hoe wordt de joystick " + (run.Profile.Joysticks.FirstOrDefault(j => j.Id == joystick.SubjectId)?.Name ?? "") + " bediend?",
                answer, Basis(joystick.Coverage), State(joystick.Status), 2, joystick.SubjectId));
        }
        foreach (var actuator in run.Actuators)
        {
            var response = actuator.Value?.SetpointChange.Value;
            var answer = response?.StartStatistics.MeanDelaySeconds is { } mean
                ? $"Start vanuit rust: gemiddeld {1000 * mean:N0} ms vanaf setpointwijziging, {response.StartStatistics.MatchedEventCount:N0} gemeten acties."
                : actuator.Explanation;
            OverviewCards.Add(new("Hoe reageert " + (run.Profile.Actuators.FirstOrDefault(a => a.Id == actuator.SubjectId)?.Name ?? "de actuator") + "?",
                answer, Basis(actuator.Coverage), State(actuator.Value?.SetpointChange.Status ?? actuator.Status), 1, actuator.SubjectId));
            var tracking = actuator.Value?.Tracking;
            var followed = tracking?.Value;
            OverviewCards.Add(new("Hoe goed volgt " + (run.Profile.Actuators.FirstOrDefault(a => a.Id == actuator.SubjectId)?.Name ?? "de actuator") + "?",
                followed?.MeanAbsoluteError is { } error
                    ? $"Tijdens beweging gemiddeld {error:N3} {tracking!.Unit} afwijking; 95% van de gemeten bewegingstijd onder {followed.Percentile95AbsoluteError:N3} {tracking.Unit}."
                    : tracking?.Explanation ?? actuator.Explanation,
                followed is null ? Basis(actuator.Coverage) : $"{followed.MovingSeconds:N1} s detecteerbare verandering; gelijke eenheid en nulreferentie vereist.",
                State(tracking?.Status ?? actuator.Status), 1, actuator.SubjectId));
            var stroke = actuator.Value?.Stroke;
            OverviewCards.Add(new("Hoeveel actuatorbereik wordt benut?",
                stroke?.Value is { } range ? $"{range.P01Percent:N1}–{range.P99Percent:N1}% van het bevestigde bereik omvat 98% van de beoordeelde tijd; {range.LowVisits + range.HighVisits:N0} grensbezoeken."
                    : stroke?.Explanation ?? actuator.Explanation,
                stroke is null ? Basis(actuator.Coverage) : Basis(stroke.Coverage), State(stroke?.Status ?? actuator.Status), 2, actuator.SubjectId));
        }
        var usage = run.ActiveUsage.Value;
        OverviewCards.Add(new("Hoe lang is de machine actief?", usage is null ? run.ActiveUsage.Explanation
            : $"{usage.ActiveSeconds / 60:N1} min actief en {usage.IdleSeconds / 60:N1} min inactief volgens de gekozen definitie.",
            run.ActiveUsage.Explanation, State(run.ActiveUsage.Status), 4));
        OverviewCards.Add(new("Wat weten we over de accuconfiguratie?", run.BatteryConfiguration.Explanation,
            Basis(run.BatteryConfiguration.Coverage), State(run.BatteryConfiguration.Status), 4));
        OverviewCards.Add(new("Is de meetdata bruikbaar?", $"{run.CanChannels.Count(c => c.Value is not null):N0} CAN-kanalen; klokkwaliteit {run.Quality.Value?.ClockQuality ?? "onbekend"}.",
            run.Quality.Explanation, State(run.Quality.Status), 3));
        OverviewStatus = "Volledige dataset · standaardanalyses · alle herkende joysticks, actuatoren en CAN-kanalen. Details kunnen een gekozen tijdvenster volgen.";
    }

    private static string Basis(AnalysisCoverage coverage) => $"{coverage.ObservedSeconds:N1} s gemeten dekking binnen {coverage.RequestedSeconds:N1} s.";
    private static string State(ModuleStatus status) => status switch
    {
        ModuleStatus.Available => "Beschikbaar", ModuleStatus.Partial => "Gedeeltelijk / indicatief",
        ModuleStatus.NeedsConfiguration => "Bevestiging nodig", ModuleStatus.MissingSignals => "Signaal ontbreekt",
        ModuleStatus.AmbiguousMapping => "Koppeling onduidelijk", ModuleStatus.InsufficientCoverage => "Onvoldoende meetbasis", _ => "Kon niet berekenen"
    };

    public void ClearDataset()
    {
        _scheduledAnalysis?.Cancel(); _analysisCalculation?.Cancel();
        _analysisRevision++; _recomputeRequested = false; _dataset = null; _standardRun = null;
        StopJoystickPlayback(); ClearOutputs(); ActiveUsage.ClearDataset();
        AvailableSignals.Clear(); OptionalSignals.Clear(); AvailableCanChannels.Clear();
        AvailableJoystickProfiles.Clear(); SelectedJoystickProfile = null; CurrentProfile = null; OverviewCards.Clear();
        HasUnsavedProfileChanges = false;
        OverviewStatus = StatusText = "Geen dataset geladen.";
    }

    public async Task CancelAndWaitAsync()
    {
        _scheduledAnalysis?.Cancel(); _analysisCalculation?.Cancel(); _recomputeRequested = false;
        _analysisRevision++;
        while (IsBusy) await Task.Delay(25);
    }
}
