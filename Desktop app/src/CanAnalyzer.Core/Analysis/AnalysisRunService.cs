using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CanAnalyzer.Core.Analysis;

/// <summary>Explicit analysis plan without presentation state or sampled numerical inputs.</summary>
public sealed partial class AnalysisRunService
{
    public StandardAnalysisRun Analyze(CanDataset dataset, AnalysisRunRequest request, CancellationToken cancellationToken = default)
        => AnalyzeCore(dataset, request, null, cancellationToken);

    public AnalysisDetailRun AnalyzeDetails(CanDataset dataset, AnalysisRunRequest request, CancellationToken cancellationToken = default)
    {
        var details = new DetailCollector();
        var run = AnalyzeCore(dataset, request, details, cancellationToken);
        return new(run, run.Joysticks.Select(m => Project(m, details.Joysticks.GetValueOrDefault(m.SubjectId))).ToArray(),
            Project(run.ActiveUsage, details.ActiveUsage));
    }

    private StandardAnalysisRun AnalyzeCore(CanDataset dataset, AnalysisRunRequest request, DetailCollector? details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scope is not ("FullDataset" or "TimeWindow") || (request.Modules & ~AnalysisModules.All) != 0)
            throw new ArgumentException("Onbekend analysebereik of analyseplan.", nameof(request));
        if (request.Scope == "FullDataset" && (request.StartSeconds.HasValue || request.EndSeconds.HasValue))
            throw new ArgumentException("Een volledige datasetanalyse heeft geen tijdvenster.", nameof(request));
        if (request.Scope == "TimeWindow" && (request.StartSeconds is { } windowStart && !double.IsFinite(windowStart) ||
            request.EndSeconds is { } windowEnd && !double.IsFinite(windowEnd) || request.StartSeconds is { } s && request.EndSeconds is { } e && e <= s))
            throw DelayInputFailure.Create(DelayInputFailureReason.InvalidTimeWindow);
        using var lease = dataset.AcquireReadLease();
        cancellationToken.ThrowIfCancellationRequested();
        var profile = request.Profile ?? AnalysisProfileResolver.Recognize(dataset);
        if (profile.SchemaVersion != 1 || profile.Confirmed && !AnalysisProfileResolver.IsApplicable(profile, dataset))
            throw new ArgumentException("Het bevestigde analyseprofiel hoort niet bij deze bron of installatie, of gebruikt een onbekende schemaversie.", nameof(request));
        if (profile.DbcSha256 != dataset.DbcSha256)
            throw new ArgumentException("Het analyseprofiel hoort bij een andere DBC.", nameof(request));
        if (!double.IsFinite(profile.MaximumGapSeconds) || profile.MaximumGapSeconds <= 0)
            throw new ArgumentException("Maximale sampleafstand moet positief zijn.", nameof(request));
        var start = double.PositiveInfinity; var end = double.NegativeInfinity;
        var internalChannels = (dataset.ImportReport?.InternalDiagnosticChannels ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in request.StartSeconds.HasValue && request.EndSeconds.HasValue ? [] : dataset.RawFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (internalChannels.Contains(frame.Channel)) continue;
            start = Math.Min(start, frame.TimeSeconds); end = Math.Max(end, frame.TimeSeconds);
        }
        if (!double.IsFinite(start)) start = end = 0;
        if ((!request.StartSeconds.HasValue || !request.EndSeconds.HasValue) && end <= start && dataset.DecodedSamples.Count > 0)
        {
            start = double.PositiveInfinity; end = double.NegativeInfinity;
            foreach (var sample in dataset.DecodedSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                start = Math.Min(start, sample.TimeSeconds); end = Math.Max(end, sample.TimeSeconds);
            }
        }
        if ((!request.StartSeconds.HasValue || !request.EndSeconds.HasValue) && end <= start)
        {
            foreach (var series in dataset.SignalSeriesByIdentity.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                series.GetData(cancellationToken);
                if (series.Time.Length == 0) continue;
                if (end <= start) { start = series.Time[0]; end = series.Time[^1]; }
                else { start = Math.Min(start, series.Time[0]); end = Math.Max(end, series.Time[^1]); }
            }
        }
        if (request.Scope == "TimeWindow") { start = request.StartSeconds ?? start; end = request.EndSeconds ?? end; }
        if (end < start) throw DelayInputFailure.Create(DelayInputFailureReason.InvalidTimeWindow);
        var duration = Math.Max(0, end - start);
        var resultBasis = dataset.SourceLogSha256 + ":" + dataset.DbcSha256 + ":" +
            JsonSerializer.Serialize(new { Profile = profile, request.Scope, StartSeconds = start, EndSeconds = end });
        var units = new Dictionary<SignalIdentity, string>();
        var neededUnits = Requested(AnalysisModules.Actuators) ? profile.Actuators.SelectMany(a => Ids(a.Command, a.Feedback, a.Stroke?.CenterSignal, a.Stroke?.HalfRangeSignal)).ToHashSet() : [];
        if (dataset.DecodedSamples is ISignalSampleLookup lookup)
        {
            foreach (var summary in lookup.GetSignalSummaries())
                if (neededUnits.Contains(summary.Latest.Identity)) units.TryAdd(summary.Latest.Identity, summary.Latest.Unit);
        }
        else if (neededUnits.Count > 0)
        {
            foreach (var sample in dataset.DecodedSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (neededUnits.Remove(sample.Identity)) units.TryAdd(sample.Identity, sample.Unit);
                if (neededUnits.Count == 0) break;
            }
        }
        var machineIds = dataset.SourceFiles.Where(s => s.MachineAssignmentQuality == "embedded-installation" ||
                s.MachineAssignmentQuality == "verified-installation" && !string.IsNullOrWhiteSpace(s.MachineAssignmentEvidence))
            .Select(s => s.MachineId).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().Distinct().ToArray();
        var mixed = machineIds.Length > 1;
        var joysticks = new List<ModuleResult<JoystickUsageSummary>>();
        foreach (var mapping in Requested(AnalysisModules.Joystick) ? profile.Joysticks : [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = Ids(mapping.X, mapping.Y);
            if (mixed) { joysticks.Add(Missing<JoystickUsageSummary>("joystick", mapping.Id, "mixed_machines", "Kies één machine voor bedieningsanalyse.", inputs)); continue; }
            joysticks.Add(Calculate("joystick", mapping.Id, inputs, () =>
            {
                var x = Resolve(mapping.X, allowInvalidated: true); var y = Resolve(mapping.Y, allowInvalidated: true);
                if (x is null || y is null)
                    return MappingIssue<JoystickUsageSummary>("joystick", mapping.Id, "missing_axis", "X en Y zijn niet eenduidig gekoppeld.", inputs);
                var calibrated = mapping.CalibrationConfirmed && mapping.XCalibration is not null && mapping.YCalibration is not null;
                var result = UsageAnalytics.AnalyzeJoystick(x, y, start, end, mapping.NeutralThreshold, mapping.FullThreshold, mapping.HistogramBins,
                    maximumGapSeconds: profile.MaximumGapSeconds, gaps: MeasurementGapSelection.ForSeries(dataset.ImportReport, x, y),
                    xCalibration: calibrated ? mapping.XCalibration : null, yCalibration: calibrated ? mapping.YCalibration : null, cancellationToken: cancellationToken);
                if (details is not null) details.Joysticks[mapping.Id] = result;
                var summary = new JoystickUsageSummary(result.IsCalibrated, result.CoveredSeconds, result.WindowSeconds,
                    result.RadiusDistribution, result.AmplitudeBands, result.InputEpisodes, result.FullHolds, result.OutsideCalibrationSeconds);
                return Result("joystick", mapping.Id, summary,
                    result.CoveredSeconds <= 0 ? ModuleStatus.InsufficientCoverage : calibrated ? CompleteStatus() : ModuleStatus.Partial,
                    result.CoveredSeconds <= 0 ? "Geen gezamenlijke meetdekking van X en Y." : calibrated
                        ? "Tijdgewogen bediening ten opzichte van bevestigde joystickgrenzen."
                        : "Relatieve verdeling beschikbaar; bevestig joystickgrenzen voor fysieke uitslag en korte correcties.",
                    inputs, result.CoveredSeconds, calibrated ? [] : ["calibration_required"]);
            }));
        }
        if (Requested(AnalysisModules.Joystick) && joysticks.Count == 0) joysticks.Add(Missing<JoystickUsageSummary>("joystick", "unmapped", "missing_signals", "Geen joystickpaar herkend; koppel X en Y in een profiel.", []));

        var actuators = new List<ModuleResult<ActuatorAnalysisResult>>();
        foreach (var mapping in Requested(AnalysisModules.Actuators) ? profile.Actuators : [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = Ids(mapping.Command, mapping.Feedback);
            if (mixed) { actuators.Add(Missing<ActuatorAnalysisResult>("actuator", mapping.Id, "mixed_machines", "Kies één machine voor actuatoranalyse.", inputs)); continue; }
            actuators.Add(Calculate("actuator", mapping.Id, inputs, () =>
            {
                var command = Resolve(mapping.Command); var feedback = Resolve(mapping.Feedback);
                if (feedback is null)
                    return MappingIssue<ActuatorAnalysisResult>("actuator", mapping.Id, "missing_feedback", "Feedback is niet eenduidig gekoppeld of de definitie moet opnieuw worden bevestigd.", inputs);
                var stroke = AnalyzeStroke(mapping, feedback, command);
                if (command is null)
                {
                    var missing = MappingIssue<MotionResponseResult>("reaction.setpoint_change", mapping.Id, "missing_command", "Setpoint is niet eenduidig gekoppeld of de definitie moet opnieuw worden bevestigd.", inputs);
                    var missingTracking = MappingIssue<MotionTrackingResult>("tracking", mapping.Id, "missing_command", missing.Explanation, inputs);
                    return Result("actuator", mapping.Id,
                        new ActuatorAnalysisResult(Identify(missing), Identify(missing with { ModuleId = "reaction.position_request" }), Identify(missingTracking)) { Stroke = Identify(stroke) },
                        stroke.Value is null ? missing.Status : ModuleStatus.Partial, missing.Explanation, inputs, stroke.Coverage.ObservedSeconds);
                }
                var gaps = MeasurementGapSelection.ForSeries(dataset.ImportReport, command, feedback);
                var response = MotionResponseAnalyzer.Analyze(command, feedback,
                    mapping.ResponseOptions with { Reference = MotionResponseReference.SetpointChange }, gaps, cancellationToken, start, end);
                cancellationToken.ThrowIfCancellationRequested();
                var covered = JointCovered(command, feedback, profile.MaximumGapSeconds, gaps, start, end, cancellationToken);
                var setpoint = Result("reaction.setpoint_change", mapping.Id, response,
                    response.AllStatistics.MatchedEventCount > 0 ? CompleteStatus() : ModuleStatus.InsufficientCoverage,
                    "Tijd tot bevestigde feedbackgrens op oorspronkelijke samples; geen onafhankelijke fysieke bewegingsstart.", inputs, covered,
                    response.AllStatistics.MatchedEventCount > 0 ? [] : ["no_accepted_events"]);
                ModuleResult<MotionResponseResult> position;
                ModuleResult<MotionTrackingResult> tracking;
                if (!UnitsMatch(mapping.Command, mapping.Feedback))
                {
                    position = Missing<MotionResponseResult>("reaction.position_request", mapping.Id, "unit_mismatch",
                        "Setpoint en feedback hebben verschillende DBC-eenheden; een expliciete conversie is nodig voordat de positie mag worden vergeleken.", inputs, ModuleStatus.NeedsConfiguration);
                    tracking = Missing<MotionTrackingResult>("tracking", mapping.Id, "unit_mismatch", position.Explanation, inputs, ModuleStatus.NeedsConfiguration);
                }
                else if (!mapping.PositionReferenceConfirmed)
                {
                    position = Missing<MotionResponseResult>("reaction.position_request", mapping.Id, "position_reference_required",
                        "Bevestig gelijke eenheid en nulreferentie van setpoint en feedback.", inputs, ModuleStatus.NeedsConfiguration);
                    tracking = Missing<MotionTrackingResult>("tracking", mapping.Id, "position_reference_required",
                        "Volgfout vereist gelijke eenheid en nulreferentie.", inputs, ModuleStatus.NeedsConfiguration);
                }
                else
                {
                    var requested = MotionResponseAnalyzer.Analyze(command, feedback,
                        mapping.ResponseOptions with { Reference = MotionResponseReference.PositionRequest }, gaps, cancellationToken, start, end);
                    cancellationToken.ThrowIfCancellationRequested();
                    position = Result("reaction.position_request", mapping.Id, requested,
                        requested.AllStatistics.MatchedEventCount > 0 ? CompleteStatus() : ModuleStatus.InsufficientCoverage,
                        "Afzonderlijke referentie: nieuwe richting buiten de marge rond feedback.", inputs, covered,
                        requested.AllStatistics.MatchedEventCount > 0 ? [] : ["no_accepted_events"]);
                    var run = Resolve(mapping.RunSignal);
                    if (mapping.RunSignal is not null && run is null)
                        tracking = Missing<MotionTrackingResult>("tracking", mapping.Id, "missing_run_signal",
                            "Het ingestelde bedrijfsstatussignaal ontbreekt; analyse zonder dit filter zou de betekenis veranderen.",
                            Ids(mapping.Command, mapping.Feedback, mapping.RunSignal));
                    else
                    {
                        var followed = MotionTrackingAnalyzer.Analyze(command, feedback, response.CommandThreshold, response.ResponseThreshold,
                            profile.MaximumGapSeconds, MeasurementGapSelection.ForSeries(dataset.ImportReport, command, feedback, run), run, mapping.RunValue, cancellationToken, start, end);
                        tracking = Result("tracking", mapping.Id, followed, followed.MovingSeconds > 0 ? CompleteStatus() : ModuleStatus.InsufficientCoverage,
                            "Tijdgewogen volgfout tijdens detecteerbare verandering; geen verdunning door lange stilstand.",
                            Ids(mapping.Command, mapping.Feedback, mapping.RunSignal), followed.CoveredSeconds,
                            followed.MovingSeconds > 0 ? [] : ["no_detected_movement"]);
                    }
                }
                var result = new ActuatorAnalysisResult(Identify(setpoint), Identify(position), Identify(tracking)) { Stroke = Identify(stroke) };
                return Result("actuator", mapping.Id, result,
                    setpoint.Status == ModuleStatus.Available && position.Status == ModuleStatus.Available && tracking.Status == ModuleStatus.Available && stroke.Status == ModuleStatus.Available
                        ? ModuleStatus.Available : ModuleStatus.Partial,
                    "Reactietijdreferenties en volgfout behouden hun eigen meetbasis en beschikbaarheid.", inputs, covered);
            }));
        }
        if (Requested(AnalysisModules.Actuators) && actuators.Count == 0) actuators.Add(Missing<ActuatorAnalysisResult>("actuator", "unmapped", "missing_signals", "Geen actuatorpaar herkend; koppel setpoint en feedback in een profiel.", []));

        // Battery configuration is also a dependency of usage forecasts when its own card is not requested.
        var batteryBasis = !Requested(AnalysisModules.Battery | AnalysisModules.ActiveUsage) ? NotRequested<BatteryConfigurationResult>("battery_configuration") : Calculate("battery_configuration", "machine", Ids(profile.Battery?.ActivePackCount), () =>
        {
            if (mixed || duration <= 0) return Missing<BatteryConfigurationResult>("battery_configuration", "machine",
                mixed ? "mixed_machines" : "no_duration", "Geen eenduidige machineperiode beschikbaar.", []);
            var count = Resolve(profile.Battery?.ActivePackCount);
            if (profile.Battery?.ActivePackCount is not null && count is null)
                return Missing<BatteryConfigurationResult>("battery_configuration", "machine", "missing_count_signal",
                    "Het ingestelde signaal voor actieve accupacks ontbreekt.", Ids(profile.Battery.ActivePackCount));
            var result = new BatteryConfigurationAnalyzer().Analyze(count, profile.Battery?.ConfirmedCount, start, end,
                profile.MaximumGapSeconds, MeasurementGapSelection.ForSeries(dataset.ImportReport, count), cancellationToken);
            return Result("battery_configuration", "machine", result,
                result.Source == BatteryCountSource.Unknown ? ModuleStatus.NeedsConfiguration : CompleteStatus(),
                result.Source == BatteryCountSource.Unknown ? "Aantal actieve accupacks onbekend; SOC-kanalen bewijzen geen packaantal."
                    : "Actieve accupacks uit expliciete bron; wijzigingen blijven zichtbaar.",
                Ids(profile.Battery?.ActivePackCount), result.KnownSeconds,
                result.Source == BatteryCountSource.Unknown ? ["battery_count_unknown"] : []);
        });
        var battery = Requested(AnalysisModules.Battery) ? batteryBasis : NotRequested<BatteryConfigurationResult>("battery_configuration");
        var usage = MappingIssue<UsageAnalysisSummary>("active_usage", "machine", "missing_signals", "Geen eenduidig activiteit- of SOC-signaal herkend.", []);
        if (Requested(AnalysisModules.ActiveUsage) && !mixed && profile.Usage is { } configuration)
        {
            var inputs = Ids(configuration.Activity, configuration.Soc, configuration.On);
            usage = Calculate("active_usage", "machine", inputs, () =>
            {
                var activity = Resolve(configuration.Activity); var soc = Resolve(configuration.Soc); var on = Resolve(configuration.On);
                if (configuration.On is not null && on is null)
                    return Missing<UsageAnalysisSummary>("active_usage", "machine", "missing_on_signal",
                        "Het ingestelde aan/uit-signaal ontbreekt; de gekozen aan-tijd wordt niet vervangen door meetdekking.", inputs);
                if ((configuration.Options.Method == ActivityDetectionMethod.SocSlope ? soc : activity) is null)
                    return Missing<UsageAnalysisSummary>("active_usage", "machine", "missing_source", "Het gekozen activiteitssignaal ontbreekt.", inputs);
                var options = configuration.Options with { StartSeconds = start, EndSeconds = end,
                    BatteryCapacityKwh = configuration.CapacityConfirmed ? configuration.Options.BatteryCapacityKwh : 1 };
                var result = new ActiveUsageAnalyzer().Analyze(activity, soc, on, options,
                    MeasurementGapSelection.ForSeries(dataset.ImportReport, configuration.Options.Method == ActivityDetectionMethod.SocSlope ? soc : activity, soc, on), cancellationToken);
                var batteryUnavailable = batteryBasis.Status is ModuleStatus.MissingSignals or ModuleStatus.Failed;
                var batteryChanged = batteryBasis.Value?.HasConfigurationChanges == true;
                var energyAvailable = configuration.CapacityConfirmed && !batteryChanged && !batteryUnavailable;
                result = result with
                {
                    BatteryConfiguration = batteryBasis.Value, EnergyAvailable = energyAvailable,
                    EnergyByState = result.EnergyByState.ToDictionary(pair => pair.Key, pair => pair.Value with { EnergyAvailable = energyAvailable }),
                    ForecastUnavailableReason = batteryUnavailable ? "battery_configuration_unavailable"
                        : batteryChanged ? "battery_configuration_changed" : !configuration.CapacityConfirmed ? "capacity_unconfirmed"
                        : !configuration.ThresholdConfirmed ? "activity_threshold_unconfirmed" : null
                };
                if (details is not null) details.ActiveUsage = result;
                var summary = new UsageAnalysisSummary(result.Intervals, result.ActiveSeconds, result.IdleSeconds,
                    result.Duration(UsageState.Unknown) + result.Duration(UsageState.UnknownActivity), result.ActivePercent,
                    result.TotalEnergy.CoveredSeconds, result.TotalEnergy.DischargeSocPoints, result.TotalEnergy.RiseSocPoints,
                    configuration.CapacityConfirmed ? options.BatteryCapacityKwh : null,
                    configuration.CapacityConfirmed ? result.TotalEnergy.NetKwh : null,
                    configuration.ThresholdConfirmed && batteryBasis.Status is not (ModuleStatus.MissingSignals or ModuleStatus.Failed) ? result.ActiveRuntimeHours : null,
                    configuration.ThresholdConfirmed && batteryBasis.Status is not (ModuleStatus.MissingSignals or ModuleStatus.Failed) ? result.RemainingActiveHours : null, result.HasOnSignal);
                var reasons = new List<string>();
                if (batteryUnavailable) { reasons.Add("battery_configuration_unavailable"); reasons.AddRange(batteryBasis.ReasonCodes); }
                if (batteryBasis.Value?.HasConfigurationChanges == true) reasons.Add("battery_configuration_changed");
                if (!configuration.ThresholdConfirmed) reasons.Add("activity_threshold_unconfirmed");
                if (!configuration.CapacityConfirmed) reasons.Add("capacity_unconfirmed");
                return Result("active_usage", "machine", summary, configuration.ThresholdConfirmed && configuration.CapacityConfirmed && !batteryUnavailable && !batteryChanged ? CompleteStatus() : ModuleStatus.Partial,
                    batteryBasis.Value?.HasConfigurationChanges == true ? "Actieve accupacks veranderen binnen de periode; er is geen accuduurprognose voor de samengestelde periode."
                        : batteryUnavailable ? "De ingestelde accuconfiguratie is niet beschikbaar; energie en prognoses ontbreken."
                        : !configuration.CapacityConfirmed ? "Capaciteit niet bevestigd; gemeten SOC blijft beschikbaar, kWh en prognoses ontbreken."
                        : configuration.ThresholdConfirmed ? "Activiteit volgens bevestigde grens; SOC-ramingen blijven schattingen."
                        : "Indicatieve activiteit volgens vermelde standaardgrens; bevestig de activiteitsdefinitie voor gebruiksramingen.",
                    inputs, duration - summary.UnknownSeconds, reasons);
            });
        }
        else if (!Requested(AnalysisModules.ActiveUsage)) usage = NotRequested<UsageAnalysisSummary>("active_usage");
        else if (mixed) usage = Missing<UsageAnalysisSummary>("active_usage", "machine", "mixed_machines", "Gebruik blijft gescheiden per machine.", []);
        var channelResults = Requested(AnalysisModules.Can) ? AnalyzeCan(dataset, profile, start, end, cancellationToken) : [];
        var quality = !Requested(AnalysisModules.Quality) ? NotRequested<DatasetQualityResult>("data_quality") : Result("data_quality", "dataset",
            new DatasetQualityResult(dataset.Completeness, dataset.ClockQuality, machineIds, dataset.ImportReport?.RejectedLines ?? 0,
                dataset.ImportReport?.Gaps ?? [], dataset.LoggerDiagnostics),
            dataset.Completeness == DatasetCompleteness.Complete && !mixed ? ModuleStatus.Available : ModuleStatus.Partial,
            "Registratietijd is de loggerklok; ontvangsttijd en klokbewijs blijven afzonderlijke brongegevens.", [], duration,
            mixed ? ["mixed_machines"] : []);
        cancellationToken.ThrowIfCancellationRequested();
        return new StandardAnalysisRun
        {
            DatasetFingerprint = dataset.SourceLogSha256 + ":" + dataset.DbcSha256, Profile = profile, Scope = request.Scope, Modules = request.Modules,
            SourceHashes = dataset.SourceFiles.Select(s => s.Sha256).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().Distinct().Order().ToArray(),
            StartSeconds = start, EndSeconds = end, Joysticks = joysticks.Select(Identify).ToArray(), Actuators = actuators.Select(Identify).ToArray(),
            CanChannels = channelResults.Select(Identify).ToArray(), ActiveUsage = Identify(usage), BatteryConfiguration = Identify(battery), Quality = Identify(quality)
        };

        bool Requested(AnalysisModules module) => (request.Modules & module) != 0;
        bool UnitsMatch(SignalIdentity? a, SignalIdentity? b) => a is null || b is null ||
            !units.TryGetValue(a, out var first) || string.IsNullOrWhiteSpace(first) ||
            !units.TryGetValue(b, out var second) || string.IsNullOrWhiteSpace(second) ||
            string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
        ModuleResult<T> MappingIssue<T>(string module, string subject, string code, string explanation, IReadOnlyList<SignalIdentity> inputs)
        {
            var alternatives = profile.MappingCandidates.Where(c => c.SubjectId == subject && c.Candidates.Count > 1).ToArray();
            if (alternatives.Length == 0) return Missing<T>(module, subject, code, explanation, inputs);
            return Missing<T>(module, subject, "ambiguous_mapping", explanation + " Kandidaten: " +
                string.Join("; ", alternatives.Select(c => c.Role + ": " + string.Join(", ", c.Candidates.Select(i => i.DisplayLabel)))) +
                ". Kies het bedoelde signaal in de keuzelijst op de detailtab en bevestig het profiel.", inputs, ModuleStatus.AmbiguousMapping);
        }
        ModuleResult<CalibratedStrokeResult> AnalyzeStroke(ActuatorAnalysisProfile mapping, SignalSeries feedback, SignalSeries? command)
        {
            var configuration = mapping.Stroke;
            var inputs = Ids(mapping.Feedback, configuration?.CenterSignal, configuration?.HalfRangeSignal, mapping.RunSignal,
                mapping.PositionReferenceConfirmed ? mapping.Command : null);
            if (configuration?.CalibrationConfirmed != true)
                return Missing<CalibratedStrokeResult>("stroke", mapping.Id, "stroke_reference_required",
                    "Bevestig een bekend nulpunt en halfbereik in de bereikinstellingen; loguitersten zijn geen fysieke grenzen.", inputs, ModuleStatus.NeedsConfiguration);
            return Calculate("stroke", mapping.Id, inputs, () =>
            {
                var center = Resolve(configuration.CenterSignal); var half = Resolve(configuration.HalfRangeSignal); var run = Resolve(mapping.RunSignal);
                if (configuration.CenterSignal is not null && center is null || configuration.HalfRangeSignal is not null && half is null || mapping.RunSignal is not null && run is null)
                    return Missing<CalibratedStrokeResult>("stroke", mapping.Id, "missing_stroke_reference",
                        "Een ingestelde referentie of bedrijfsstatus ontbreekt of moet opnieuw worden bevestigd; het filter wordt niet weggelaten.", inputs);
                if (!UnitsMatch(mapping.Feedback, configuration.CenterSignal) || !UnitsMatch(mapping.Feedback, configuration.HalfRangeSignal))
                    return Missing<CalibratedStrokeResult>("stroke", mapping.Id, "unit_mismatch",
                        "Feedback, nulpunt en halfbereik moeten dezelfde DBC-eenheid hebben; conversie is niet ingesteld.", inputs, ModuleStatus.NeedsConfiguration);
                var incompatibleSetpoint = mapping.PositionReferenceConfirmed && !UnitsMatch(mapping.Command, mapping.Feedback);
                var setpoint = mapping.PositionReferenceConfirmed && !incompatibleSetpoint ? command : null;
                var result = CalibratedStrokeAnalytics.Analyze(feedback,
                    new(configuration.ManualCenter, configuration.HalfRange ?? double.NaN, center, half, setpoint, run, mapping.RunValue, profile.MaximumGapSeconds),
                    start, end, configuration.EdgePercent, configuration.HistogramBins, gaps: MeasurementGapSelection.ForSeries(dataset.ImportReport, feedback, center, half, setpoint, run), cancellationToken: cancellationToken);
                return Result("stroke", mapping.Id, result, result.CoveredSeconds > 0 ? incompatibleSetpoint ? ModuleStatus.Partial : CompleteStatus() : ModuleStatus.InsufficientCoverage,
                    incompatibleSetpoint ? "Slagbenutting beschikbaar; setpointvergelijking ontbreekt vanwege verschillende DBC-eenheden."
                        : "Tijdgewogen benutting van het bevestigde actuatorbereik, inclusief grensbezoeken en tijd buiten bereik. Setpointvergelijking vereist dezelfde bevestigde eenheid en nulreferentie.",
                    inputs, result.CoveredSeconds, result.CoveredSeconds <= 0 ? ["no_joint_coverage"] : incompatibleSetpoint ? ["unit_mismatch"] : []);
            });
        }
        ModuleResult<T> Identify<T>(ModuleResult<T> result)
        {
            var unit = result.ModuleId.StartsWith("reaction.", StringComparison.Ordinal) ? "s"
                : result.ModuleId == "tracking" ? InputUnit(1) : result.ModuleId == "stroke" ? "%" : "mixed";
            IReadOnlyDictionary<string, string> valueUnits = result.ModuleId switch
            {
                "joystick" => new Dictionary<string, string> { ["duration"] = "s", ["normalizedAmplitude"] = "1", ["proportion"] = "%", ["events"] = "count" },
                "reaction.setpoint_change" or "reaction.position_request" => new Dictionary<string, string> { ["delay"] = "s", ["commandThreshold"] = InputUnit(0), ["responseThreshold"] = InputUnit(1), ["events"] = "count" },
                "tracking" => new Dictionary<string, string> { ["absoluteError"] = unit, ["coverageAndMovement"] = "s" },
                "stroke" => new Dictionary<string, string> { ["normalizedPosition"] = "%", ["duration"] = "s", ["visits"] = "count", ["proportion"] = "%" },
                "can" => new Dictionary<string, string> { ["frames"] = "count", ["cycle"] = "ms", ["rate"] = "frames/s", ["busLoad"] = "%" },
                "active_usage" => new Dictionary<string, string> { ["duration"] = "s", ["socChange"] = "percentage-point", ["energy"] = "kWh", ["runtime"] = "h", ["activeProportion"] = "%" },
                "battery_configuration" => new Dictionary<string, string> { ["activePacks"] = "count", ["duration"] = "s" },
                _ => new Dictionary<string, string> { ["coverage"] = "s" }
            };
            return result with { ResultId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                resultBasis + ":" + result.ModuleId + ":" + result.SubjectId + ":" + result.MethodVersion))).ToLowerInvariant(),
                Unit = unit, ValueUnits = valueUnits,
                Coverage = result.Status != ModuleStatus.NotRequested && result.Coverage.RequestedSeconds == 0 && duration > 0
                    ? new(duration, result.Coverage.ObservedSeconds, result.Coverage.EligibleSeconds) : result.Coverage };
            string InputUnit(int index) => result.Inputs.Count > index && units.TryGetValue(result.Inputs[index], out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : "unknown";
        }
        ModuleStatus CompleteStatus() => dataset.Completeness == DatasetCompleteness.Partial ? ModuleStatus.Partial : ModuleStatus.Available;
        SignalSeries? Resolve(SignalIdentity? id, bool allowInvalidated = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id is null || !allowInvalidated && profile.InvalidatedSignals.Contains(id) || !dataset.SignalSeriesByIdentity.TryGetValue(id, out var signal)) return null;
            signal.GetData(cancellationToken); // Exact source arrays; never ForAnalysis/downsampling.
            return signal;
        }
        ModuleResult<T> Result<T>(string module, string subject, T value, ModuleStatus status, string note,
            IReadOnlyList<SignalIdentity> inputs, double observed, IReadOnlyList<string>? reasons = null) => new()
        {
            ModuleId = module, SubjectId = subject, Status = status, Value = value, Explanation = note,
            Inputs = inputs, Coverage = new(duration, observed, observed), ReasonCodes = reasons ?? []
        };
        ModuleResult<T> Calculate<T>(string module, string subject, IReadOnlyList<SignalIdentity> inputs, Func<ModuleResult<T>> calculate)
        {
            try { cancellationToken.ThrowIfCancellationRequested(); return calculate(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or InvalidDataException)
            {
                var reason = ex is ArgumentException argument ? DelayInputFailure.GetTelemetryCode(argument) : "invalid_input";
                return Missing<T>(module, subject, reason, ex.Message, inputs, ModuleStatus.Failed);
            }
        }
    }

    private static SignalIdentity[] Ids(params SignalIdentity?[] identities) => identities.OfType<SignalIdentity>().Distinct().ToArray();
    private static ModuleResult<T> NotRequested<T>(string module) => Missing<T>(module, "not_requested", "not_requested",
        "Deze module maakt geen deel uit van het aangevraagde analyseplan.", [], ModuleStatus.NotRequested);
    private static ModuleResult<T> Missing<T>(string module, string subject, string code, string note,
        IReadOnlyList<SignalIdentity> inputs, ModuleStatus status = ModuleStatus.MissingSignals) => new()
        { ModuleId = module, SubjectId = subject, Status = status, ReasonCodes = [code], Explanation = note, Inputs = inputs };

    private static double JointCovered(SignalSeries a, SignalSeries b, double maximumGap, IReadOnlyList<MeasurementGap> gaps,
        double windowStart, double windowEnd, CancellationToken token)
    {
        if (a.Time.Length < 2 || b.Time.Length < 2) return 0;
        var ar = new TimedSignalReader(a, maximumGap, gaps); var br = new TimedSignalReader(b, maximumGap, gaps);
        var end = Math.Min(windowEnd, Math.Min(a.Time[^1], b.Time[^1])); var cursor = Math.Max(windowStart, Math.Max(a.Time[0], b.Time[0]));
        var sorted = gaps.OrderBy(g => g.StartSeconds).ToArray(); var gi = 0; var covered = 0d;
        while (cursor < end)
        {
            token.ThrowIfCancellationRequested();
            var next = Math.Min(ar.NextBoundary(cursor, end), br.NextBoundary(cursor, end));
            while (gi < sorted.Length && sorted[gi].EndSeconds <= cursor) gi++;
            var inGap = gi < sorted.Length && sorted[gi].StartSeconds <= cursor;
            if (gi < sorted.Length) next = Math.Min(next, inGap ? sorted[gi].EndSeconds : sorted[gi].StartSeconds);
            if (next <= cursor) break;
            if (!inGap && ar.TryValue(cursor, out _) && br.TryValue(cursor, out _)) covered += next - cursor;
            cursor = next;
        }
        return covered;
    }

    private static IReadOnlyList<ModuleResult<CanChannelResult>> AnalyzeCan(CanDataset dataset, AnalysisProfile profile,
        double windowStart, double windowEnd, CancellationToken token)
    {
        var channels = new SortedDictionary<string, ChannelAccumulator>(StringComparer.Ordinal);
        var requestedDuration = Math.Max(0, windowEnd - windowStart);
        var internalChannels = (dataset.ImportReport?.InternalDiagnosticChannels ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ratesKnown = profile.CanBitratesConfirmed && double.IsFinite(profile.CanBitrateKbps) && profile.CanBitrateKbps > 0
            && double.IsFinite(profile.CanDataBitrateKbps) && profile.CanDataBitrateKbps > 0;
        foreach (var frame in dataset.RawFrames)
        {
            token.ThrowIfCancellationRequested();
            if (internalChannels.Contains(frame.Channel)) continue;
            if (frame.TimeSeconds < windowStart || frame.TimeSeconds > windowEnd) continue;
            if (!channels.TryGetValue(frame.Channel, out var channel)) channels.Add(frame.Channel, channel = new()
            { Gaps = MeasurementGapSelection.Union(MeasurementGapSelection.ForChannel(dataset.ImportReport, frame.Channel)) });
            channel.Observe(frame, ratesKnown ? profile.CanBitrateKbps * 1000 : null, profile.CanDataBitrateKbps * 1000);
        }
        if (channels.Count == 0) return [Missing<CanChannelResult>("can", "unmapped", "no_frames_in_scope",
            "Geen machine-CAN-frames binnen dit analysebereik.", [], ModuleStatus.InsufficientCoverage) with { Coverage = new(requestedDuration, 0, 0) }];
        return channels.Select(pair =>
        {
            var c = pair.Value; var duration = c.End - c.Start;
            var covered = duration;
            var previousGapEnd = c.Start;
            foreach (var gap in MeasurementGapSelection.ForChannel(dataset.ImportReport, pair.Key).OrderBy(g => g.StartSeconds))
            {
                var from = Math.Max(previousGapEnd, Math.Max(c.Start, gap.StartSeconds)); var to = Math.Min(c.End, gap.EndSeconds);
                if (to > from) covered -= to - from;
                previousGapEnd = Math.Max(previousGapEnd, to);
            }
            covered = Math.Max(0, covered);
            var reasons = new List<string>();
            if (!ratesKnown) reasons.Add("bitrates_unconfirmed");
            if (covered < requestedDuration) reasons.Add("coverage_incomplete");
            var value = new CanChannelResult(c.Count, c.Start, c.End, c.Errors, c.Remotes,
                covered > 0 ? c.Count / covered : null, ratesKnown && covered > 0 ? 100 * c.TransmissionSeconds / covered : null, ratesKnown,
                c.Streams.OrderBy(s => s.Key.Id).ThenBy(s => s.Key.Format).ThenBy(s => s.Key.Extended).ThenBy(s => s.Key.Direction)
                    .Select(s => s.Value.Result(s.Key.Id, s.Key.Extended, s.Key.Format, s.Key.Direction)).ToArray(),
                c.Minutes.OrderBy(m => m.Key).Select(m => new CanMinuteSummary(m.Key, m.Value)).ToArray());
            return new ModuleResult<CanChannelResult> { ModuleId = "can", SubjectId = pair.Key,
                Status = duration <= 0 ? ModuleStatus.InsufficientCoverage : ratesKnown && covered >= requestedDuration ? ModuleStatus.Available : ModuleStatus.Partial, Value = value,
                Coverage = new(requestedDuration, covered, covered),
                ReasonCodes = reasons, Explanation = ratesKnown
                    ? "Alle originele frames; busbelasting is een schatting met 20% bitstuffing op bevestigde bitrates. Meetgaten tellen niet als dekking."
                    : "Alle originele frames op dit kanaal; bevestig bitrates voor een busbelastingsschatting. Meetgaten tellen niet als dekking." };
        }).ToArray();
    }

    private sealed class ChannelAccumulator
    {
        public IReadOnlyList<MeasurementGap> Gaps { get; init; } = [];
        public long Count, Errors, Remotes;
        public double Start = double.PositiveInfinity, End = double.NegativeInfinity;
        public double TransmissionSeconds;
        public Dictionary<(uint Id, bool Extended, CanFrameFormat Format, CanFrameDirection Direction), StreamAccumulator> Streams { get; } = [];
        public Dictionary<double, long> Minutes { get; } = [];
        public void Observe(RawCanFrame frame, double? bitrate, double dataBitrate)
        {
            Count++; Start = Math.Min(Start, frame.TimeSeconds); End = Math.Max(End, frame.TimeSeconds);
            if (frame.Kind == CanFrameKind.Error || frame.Type.Contains("err", StringComparison.OrdinalIgnoreCase)) Errors++;
            if (frame.Kind == CanFrameKind.Remote || frame.Type.Contains("rtr", StringComparison.OrdinalIgnoreCase)) Remotes++;
            var key = (frame.Id, frame.IsExtended, frame.FrameFormat, frame.Direction);
            if (!Streams.TryGetValue(key, out var stream)) Streams.Add(key, stream = new());
            stream.Observe(frame.TimeSeconds, Gaps);
            var minute = Math.Floor(frame.TimeSeconds / 60) * 60;
            Minutes[minute] = Minutes.GetValueOrDefault(minute) + 1;
            if (bitrate is { } arbitration)
            {
                if (frame.FrameFormat == CanFrameFormat.Classic)
                    TransmissionSeconds += ((frame.IsExtended ? 67d : 47d) + frame.PayloadLength * 8d + 3d) * 1.20 / arbitration;
                else
                    TransmissionSeconds += (frame.IsExtended ? 41d : 23d) * 1.20 / arbitration
                        + (frame.PayloadLength * 8d + (frame.PayloadLength <= 16 ? 17d : 21d) + 13d) * 1.20 / (frame.BitRateSwitch ? dataBitrate : arbitration);
            }
        }
    }
    private sealed class StreamAccumulator
    {
        private long _count, _cycles, _nonIncreasing;
        private double _previous, _sum, _minimum = double.PositiveInfinity, _maximum = double.NegativeInfinity;
        public void Observe(double time, IReadOnlyList<MeasurementGap> gaps)
        {
            if (_count++ > 0)
            {
                var dt = (time - _previous) * 1000;
                if (dt <= 0) _nonIncreasing++;
                else if (!CrossesGap(gaps, _previous, time))
                { _cycles++; _sum += dt; _minimum = Math.Min(_minimum, dt); _maximum = Math.Max(_maximum, dt); }
            }
            _previous = time;
        }
        private static bool CrossesGap(IReadOnlyList<MeasurementGap> gaps, double from, double to)
        {
            var low = 0; var high = gaps.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (gaps[middle].EndSeconds <= from) low = middle + 1; else high = middle;
            }
            return low < gaps.Count && gaps[low].StartSeconds < to;
        }
        public CanStreamSummary Result(uint id, bool extended, CanFrameFormat format, CanFrameDirection direction) =>
            new(id, extended, format, direction, _count, _cycles, _cycles > 0 ? _sum / _cycles : null,
                _cycles > 0 ? _minimum : null, _cycles > 0 ? _maximum : null, _nonIncreasing);
    }
}
