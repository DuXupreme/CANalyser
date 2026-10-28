using System.Text.Json;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.ViewModels;

public partial class JoystickAnalyticsViewModel
{
    private CanDataset? _detailCacheDataset;
    private readonly Dictionary<string, (string Key, AnalysisDetailRun Run)> _detailCache = [];
    public ModuleResult<JoystickUsageResult>? JoystickModuleResult { get; private set; }
    public ModuleResult<ActuatorAnalysisResult>? ReactionModuleResult { get; private set; }

    private AnalysisProfile DetailProfile() => (CurrentProfile ?? AnalysisProfileResolver.Recognize(_dataset!)) with
    {
        Id = CurrentProfile?.Id ?? "interactive", Confirmed = false, MaximumGapSeconds = UsageMaximumGapSeconds,
        Joysticks = [], Actuators = [], Usage = null, Battery = null
    };

    private async Task<AnalysisDetailRun> AnalyzeDetailsAsync(AnalysisProfile profile, AnalysisModules modules)
    {
        var dataset = _dataset!;
        if (!ReferenceEquals(_detailCacheDataset, dataset)) { _detailCache.Clear(); _detailCacheDataset = dataset; }
        var window = AnalysisWindow;
        var request = new AnalysisRunRequest { Profile = profile, Modules = modules,
            Scope = UseJoystickTimeWindow ? "TimeWindow" : "FullDataset", StartSeconds = window.Start, EndSeconds = window.End };
        var key = JsonSerializer.Serialize(request);
        var slot = modules + ":" + string.Join(";", profile.Joysticks.Select(p => p.Id).Concat(profile.Actuators.Select(p => p.Id)));
        if (_detailCache.TryGetValue(slot, out var cached) && cached.Key == key) return cached.Run;
        var token = _analysisCalculation?.Token ?? default;
        var run = await ComputeAsync(() => new AnalysisRunService().AnalyzeDetails(dataset, request, token));
        _detailCache[slot] = (key, run);
        return run;
    }

    private SignalIdentity? DetailIdentity(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        if (TryGetSeries(label, out var series)) return series.Identity;
        var retained = CurrentProfile?.SignalDefinitions.FirstOrDefault(s => s.Identity.DisplayLabel == label)?.Identity;
        return retained ?? throw new ArgumentException($"Signaal niet beschikbaar: {label}");
    }

    private MotionResponseOptions DetailResponseOptions() => new()
    {
        MaxDelaySeconds = DelaySearchRangeSeconds,
        ResponseThresholdAbsolute = UseAutomaticResponseThreshold ? null : ResponseThresholdAbsolute,
        PositionToleranceAbsolute = UseAutomaticPositionTolerance ? null : PositionToleranceAbsolute
    };

    private ActuatorAnalysisProfile DetailActuator(int index)
    {
        var settings = ActuatorRanges[index];
        var feedback = new[] { SelectedActuatorLeftSignal, SelectedActuatorRightSignal, SelectedActuatorFrontSignal }[index];
        var commandId = DetailIdentity(settings.SetpointSignal); var feedbackId = DetailIdentity(feedback);
        var saved = CurrentProfile?.Actuators.FirstOrDefault(a => a.Command == commandId && a.Feedback == feedbackId);
        return new("detail.actuator." + index, settings.Name, commandId, feedbackId)
        {
            RunSignal = DetailIdentity(settings.RunSignal), RunValue = settings.RunValue,
            PositionReferenceConfirmed = saved?.PositionReferenceConfirmed == true ||
                PositionReferenceConfirmed && commandId == DetailIdentity(SelectedCommandSignal) && feedbackId == DetailIdentity(SelectedResponseSignal),
            ResponseOptions = DetailResponseOptions(),
            Stroke = new()
            {
                CalibrationConfirmed = settings.ManualCenter.HasValue || !string.IsNullOrWhiteSpace(settings.CenterSignal),
                ManualCenter = settings.ManualCenter, HalfRange = settings.HalfRange,
                CenterSignal = DetailIdentity(settings.CenterSignal), HalfRangeSignal = DetailIdentity(settings.HalfRangeSignal),
                EdgePercent = StrokeEdgePercent, HistogramBins = HistogramBins
            }
        };
    }
}
