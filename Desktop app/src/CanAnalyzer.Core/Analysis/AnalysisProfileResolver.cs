using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Utilities;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CanAnalyzer.Core.Analysis;

/// <summary>Conservative deterministic mapping; ambiguous candidates remain explicit.</summary>
public static class AnalysisProfileResolver
{
    public static bool IsApplicable(AnalysisProfile profile, CanDataset dataset)
    {
        if (profile.SchemaVersion != 1 || !string.Equals(profile.DbcSha256, dataset.DbcSha256, StringComparison.OrdinalIgnoreCase)) return false;
        return AppliesToHardware(profile, dataset);
    }

    private static bool AppliesToHardware(AnalysisProfile profile, CanDataset dataset)
    {
        if (profile.InstallationId is { Length: > 0 } installation)
            return dataset.SourceFiles.Count > 0 && dataset.SourceFiles.All(s => HasVerifiedAssignment(s) && s.InstallationId == installation);
        if (profile.MachineId is { Length: > 0 } machine)
            return dataset.SourceFiles.Count > 0 && dataset.SourceFiles.All(s => HasVerifiedAssignment(s) && s.MachineId == machine);
        return profile.SourceLogSha256 is { Length: > 0 } source && source.Equals(dataset.SourceLogSha256, StringComparison.OrdinalIgnoreCase);
    }

    public static AnalysisProfile Resolve(CanDataset dataset, IEnumerable<AnalysisProfile>? profiles = null)
    {
        var candidates = profiles?.Where(p => p.SchemaVersion == 1 && p.Confirmed && AppliesToHardware(p, dataset))
            .OrderByDescending(p => p.Version).ToArray() ?? [];
        var exact = candidates.FirstOrDefault(p => p.DbcSha256.Equals(dataset.DbcSha256, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var previous = candidates.FirstOrDefault(p => p.SignalDefinitions.Count > 0 && dataset.SignalDefinitionHashes.Count > 0);
        if (previous is null) return Recognize(dataset);
        var definitions = previous.SignalDefinitions.ToDictionary(p => p.Identity, p => p.DefinitionSha256);
        bool Unchanged(SignalIdentity? id) => id is not null && definitions.TryGetValue(id, out var old) &&
            dataset.SignalDefinitionHashes.TryGetValue(id, out var current) && old == current;
        SignalIdentity? Present(SignalIdentity? id) => id is not null && dataset.SignalSeriesByIdentity.ContainsKey(id) ? id : null;
        var recognized = Recognize(dataset);
        var joysticks = previous.Joysticks.Select(p => p with { X = Present(p.X), Y = Present(p.Y),
            CalibrationConfirmed = p.CalibrationConfirmed && Unchanged(p.X) && Unchanged(p.Y) }).ToList();
        joysticks.AddRange(recognized.Joysticks.Where(p => joysticks.All(j => j.Id != p.Id)));
        var actuators = previous.Actuators.Select(p => p with { Command = Unchanged(p.Command) ? Present(p.Command) : null,
            Feedback = Unchanged(p.Feedback) ? Present(p.Feedback) : null,
            RunSignal = p.RunSignal,
            PositionReferenceConfirmed = p.PositionReferenceConfirmed && Unchanged(p.Command) && Unchanged(p.Feedback) &&
                (p.RunSignal is null || Unchanged(p.RunSignal)),
            Stroke = p.Stroke is { } stroke ? stroke with { CalibrationConfirmed = stroke.CalibrationConfirmed && Unchanged(p.Feedback) &&
                (stroke.CenterSignal is null || Unchanged(stroke.CenterSignal)) && (stroke.HalfRangeSignal is null || Unchanged(stroke.HalfRangeSignal)) &&
                (p.RunSignal is null || Unchanged(p.RunSignal)) } : null,
            ResponseOptions = Unchanged(p.Command) && Unchanged(p.Feedback) ? p.ResponseOptions : new MotionResponseOptions() }).ToList();
        actuators.AddRange(recognized.Actuators.Where(p => actuators.All(a => a.Id != p.Id)));
        var usage = previous.Usage is { } u ? u with
        {
            Activity = u.Activity, Soc = u.Soc, On = u.On,
            ThresholdConfirmed = u.ThresholdConfirmed && (u.Activity is null || Unchanged(u.Activity)) && (u.Soc is null || Unchanged(u.Soc)) && (u.On is null || Unchanged(u.On)),
            Options = (u.Activity is null || Unchanged(u.Activity)) && (u.Soc is null || Unchanged(u.Soc)) && (u.On is null || Unchanged(u.On))
                ? u.Options : new ActiveUsageOptions { Method = u.Options.Method, BatteryCapacityKwh = u.Options.BatteryCapacityKwh }
        } : recognized.Usage;
        return previous with { DbcSha256 = dataset.DbcSha256, Joysticks = joysticks, Actuators = actuators, Usage = usage,
            ConfirmedDecoderChannels = [],
            SignalDefinitions = recognized.SignalDefinitions,
            MappingCandidates = recognized.MappingCandidates,
            InvalidatedSignals = previous.InvalidatedSignals.Concat(previous.SignalDefinitions.Select(d => d.Identity).Where(id => !Unchanged(id))).Distinct().ToArray(),
            Battery = previous.Battery,
            Evidence = "DBC gewijzigd: alleen ongewijzigde signaaldefinities behouden hun bevestiging. Gewijzigde schaal, offset, eenheid of bitindeling vereist opnieuw bevestigen." };
    }

    public static IReadOnlyDictionary<SignalIdentity, string> BuildDefinitionHashes(CanDataset dataset, DbcDatabase database)
    {
        var result = new Dictionary<SignalIdentity, string>();
        foreach (var id in dataset.SignalSeriesByIdentity.Keys)
        {
            var candidates = database.Messages.Where(m => m.NormalizedFrameId == id.FrameId && m.IsExtendedFrame == id.IsExtended && m.Name == id.MessageName).Take(2).ToArray();
            if (candidates.Length == 0 && id.IsExtended)
                candidates = database.Messages.Where(m => m.IsExtendedFrame && m.Name == id.MessageName &&
                    CanIdUtilities.ExtractJ1939Pgn(m.NormalizedFrameId) == CanIdUtilities.ExtractJ1939Pgn(id.FrameId)).Take(2).ToArray();
            var message = candidates.Length == 1 ? candidates[0] : null;
            var signals = message?.Signals.Where(s => s.Name == id.SignalName).Take(2).ToArray() ?? [];
            var signal = signals.Length == 1 ? signals[0] : null;
            if (signal is null || message is null) continue;
            var definition = JsonSerializer.Serialize(new { message.Dlc, message.SuppressDecoding, Signal = signal });
            result[id] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition)));
        }
        return result;
    }

    public static AnalysisProfile Recognize(CanDataset dataset)
    {
        var ids = dataset.SignalSeriesByIdentity.Keys.OrderBy(i => i.DisplayLabel, StringComparer.Ordinal).ToArray();
        var joysticks = new List<JoystickAnalysisProfile>();
        var actuators = new List<ActuatorAnalysisProfile>();
        var candidates = new List<SignalRoleCandidates>();
        foreach (var group in ids.GroupBy(i => (i.Channel, i.FrameFormat, i.IsExtended, i.FrameId, i.MessageName)))
        {
            var signals = group.ToArray();
            var key = $"{group.Key.Channel}:{group.Key.FrameFormat}:{group.Key.IsExtended}:{group.Key.FrameId:X}:{group.Key.MessageName}";
            if (signals.Any(i => Has(i.MessageName + "." + i.SignalName, "joystick", "joy")))
            {
                var xs = signals.Where(i => HasAxisToken(i.SignalName, "pos_x", "joystick_x", "axisx") || i.SignalName.EndsWith("_x", StringComparison.OrdinalIgnoreCase)).ToArray();
                var ys = signals.Where(i => HasAxisToken(i.SignalName, "pos_y", "joystick_y", "axisy") || i.SignalName.EndsWith("_y", StringComparison.OrdinalIgnoreCase)).ToArray();
                RetainCandidates(key, "joystick.x", xs); RetainCandidates(key, "joystick.y", ys);
                if (xs.Length + ys.Length > 0) joysticks.Add(new(key, group.Key.MessageName, Unique(xs), Unique(ys)));
            }
            if (!signals.Any(i => Has(i.MessageName, "actuator", "act", "left", "right", "front", "links", "rechts", "voor"))) continue;
            var feedback = signals.Where(i => Has(i.SignalName, "actualpos", "feedback", "measuredpos", "position", "encoder") &&
                !Has(i.SignalName, "target", "setpoint", "command", "desired", "offset", "calibr", "status", "error")).ToArray();
            var commands = signals.Where(i => Has(i.SignalName, "realpos", "setpoint", "target", "command", "desired") &&
                !Has(i.SignalName, "force", "current", "temperature", "status", "error")).ToArray();
            RetainCandidates(key, "actuator.command", commands); RetainCandidates(key, "actuator.feedback", feedback);
            if (feedback.Length + commands.Length > 0)
                actuators.Add(new(key, group.Key.MessageName, Unique(commands), Unique(feedback)));
        }
        var socCandidates = ids.Where(i => Has(i.SignalName, "soc") && !Has(i.SignalName, "error", "limit", "min", "max")).ToArray();
        var activityCandidates = ids.Where(i => Has(i.SignalName, "rpm", "motorspeed", "motor_speed")).ToArray();
        RetainCandidates("machine", "usage.soc", socCandidates); RetainCandidates("machine", "usage.activity", activityCandidates);
        var soc = Unique(socCandidates); var activity = Unique(activityCandidates);
        var machineIds = dataset.SourceFiles.Select(s => HasVerifiedAssignment(s) ? s.MachineId : null).Distinct().ToArray();
        var installationIds = dataset.SourceFiles.Select(s => HasVerifiedAssignment(s) ? s.InstallationId : null).Distinct().ToArray();
        return new AnalysisProfile
        {
            Id = "recognized:" + dataset.DbcSha256, DbcSha256 = dataset.DbcSha256,
            SignalDefinitions = dataset.SignalDefinitionHashes.Select(p => new SignalDefinitionReference(p.Key, p.Value)).ToArray(),
            MappingCandidates = candidates,
            SourceLogSha256 = dataset.SourceLogSha256,
            MachineId = machineIds.Length == 1 ? machineIds[0] : null,
            InstallationId = installationIds.Length == 1 ? installationIds[0] : null,
            Joysticks = joysticks, Actuators = actuators,
            Usage = activity is null && soc is null ? null : new UsageAnalysisProfile
            {
                Activity = activity, Soc = soc,
                Options = new ActiveUsageOptions { Method = activity is null ? ActivityDetectionMethod.SocSlope : ActivityDetectionMethod.SignalThreshold }
            }
        };
        void RetainCandidates(string subject, string role, SignalIdentity[] values)
        { if (values.Length > 1) candidates.Add(new(subject, role, values)); }
    }

    private static SignalIdentity? Unique(IEnumerable<SignalIdentity> candidates)
    {
        var values = candidates.Take(2).ToArray();
        return values.Length == 1 ? values[0] : null;
    }
    private static bool Has(string text, params string[] tokens) => tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    private static bool HasAxisToken(string text, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            for (var start = 0; start < text.Length;)
            {
                var index = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var end = index + token.Length;
                // Pos_Y is an axis; Pos_Yaw is a different signal, not a second Y candidate.
                if (end == text.Length || !char.IsLetterOrDigit(text[end])) return true;
                start = end;
            }
        }
        return false;
    }
    private static bool HasVerifiedAssignment(SourceLogFile source) => source.MachineAssignmentQuality == "embedded-installation" ||
        source.MachineAssignmentQuality == "verified-installation" && !string.IsNullOrWhiteSpace(source.MachineAssignmentEvidence);
}
