using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

public enum ComparisonAggregation { WeightedMean, Percentage, EventMean, EventP95, None }
public sealed record ComparisonTrendPoint(double StartSeconds, double EndSeconds, double CoveredSeconds, double Integral)
{
    public double? Value => CoveredSeconds > 0 ? Integral / CoveredSeconds : null;
    public bool BreakBefore { get; init; }
}
public sealed record ComparisonMetric(string Key, string Label, string Unit, ComparisonAggregation Aggregation,
    double? Value, double Numerator, double Denominator, string Compatibility,
    IReadOnlyList<ComparisonTrendPoint> Trend, IReadOnlyList<double>? AcceptedEvents = null,
    double? Minimum = null, double? Maximum = null, string Note = "")
{
    /// <summary>Bounded display selection, prepared while reading; original minute summaries remain intact.</summary>
    public IReadOnlyList<ComparisonTrendPoint>? DisplayTrend { get; init; }
}

public sealed record AnalysisComparisonBundle
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public int SchemaVersion { get; init; }
    public string MachineId { get; init; } = "";
    public string LoggerId { get; init; } = "";
    public string ClockQuality { get; init; } = "unverified";
    public DateTimeOffset? StartUtc { get; init; }
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public bool CanAlignUtc => ClockQuality == "verified" && StartUtc.HasValue;
    public bool SourceIdentityComplete { get; init; }
    public IReadOnlyList<string> SourceIds { get; init; } = [];
    public bool FullDataset { get; init; }
    public string Completeness { get; init; } = "unknown";
    public IReadOnlyList<ComparisonMetric> Metrics { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public DateTimeOffset? FirstUtc => CanAlignUtc ? StartUtc!.Value.AddSeconds(StartSeconds) : null;
    public DateTimeOffset? LastUtc => CanAlignUtc ? StartUtc!.Value.AddSeconds(EndSeconds) : null;
}

public sealed record ComparisonAggregate(double? Value, string Unit, int IncludedBundles, int DuplicateBundles,
    double Denominator, string Explanation);

/// <summary>Conservative aggregation: uncertain inputs remain useful side by side, never as a made-up total.</summary>
public static class AnalysisBundleComparison
{
    public static ComparisonAggregate Aggregate(IReadOnlyList<AnalysisComparisonBundle> bundles, string metricKey)
    {
        var selected = bundles.Select(bundle => (Bundle: bundle, Metric: bundle.Metrics.FirstOrDefault(metric => metric.Key == metricKey))).ToArray();
        var unit = selected.FirstOrDefault(item => item.Metric is not null).Metric?.Unit ?? "";
        ComparisonAggregate Missing(string reason) => new(null, unit, 0, 0, 0, reason);
        if (selected.Length == 0) return Missing("Selecteer ten minste één pakket.");
        if (selected.Any(item => item.Metric is null)) return Missing("Deze uitkomst ontbreekt in een geselecteerd pakket; individuele resultaten blijven zichtbaar.");
        if (selected.Any(item => item.Metric!.Aggregation == ComparisonAggregation.None))
            return Missing("Deze uitkomst wordt alleen naast elkaar getoond; capaciteiten, energie, duren en prognoses worden niet automatisch opgeteld.");
        if (selected.Any(item => item.Bundle.SchemaVersion < 3 || !item.Bundle.FullDataset))
            return Missing("Oud of beperkt exportschema: afzonderlijk vergelijkbaar, onvoldoende gegevens voor samenvoegen.");
        if (selected.Any(item => !item.Bundle.SourceIdentityComplete || item.Bundle.SourceIds.Count == 0))
            return Missing("Volledige bronidentiteit ontbreekt; dubbeltelling kan niet worden uitgesloten.");
        if (selected.Any(item => string.IsNullOrWhiteSpace(item.Bundle.MachineId)) ||
            selected.Select(item => item.Bundle.MachineId).Distinct(StringComparer.Ordinal).Count() != 1)
            return Missing("Kies één bekende machine voor een gezamenlijke uitkomst.");
        if (selected.Select(item => item.Metric!.Compatibility).Distinct(StringComparer.Ordinal).Count() != 1 ||
            selected.Select(item => item.Metric!.Aggregation).Distinct().Count() != 1 ||
            selected.Select(item => item.Metric!.Unit).Distinct(StringComparer.Ordinal).Count() != 1)
            return Missing("Niet verenigbaar (" + string.Join(", ", selected.Select(item => item.Bundle.Name)) +
                "): methode, eenheid, DBC, kalibratie, referentie, filter of betekenis verschilt.");

        var unique = new List<(AnalysisComparisonBundle Bundle, ComparisonMetric Metric)>();
        var duplicates = 0;
        foreach (var item in selected)
        {
            var sources = item.Bundle.SourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var duplicate = false;
            foreach (var previous in unique)
            {
                if (sources.SetEquals(previous.Bundle.SourceIds)) { duplicate = true; break; }
                if (sources.Overlaps(previous.Bundle.SourceIds))
                    return Missing($"Gedeeltelijk overlappende bronsets: {previous.Bundle.Name} en {item.Bundle.Name}. Sluit een conflicterend pakket expliciet uit voordat je samenvoegt.");
            }
            if (duplicate) duplicates++;
            else unique.Add((item.Bundle, item.Metric!));
        }
        if (unique.Count > 1 && unique.Any(item => !item.Bundle.CanAlignUtc))
            return Missing("Onbevestigde loggerklok: relatieve vergelijking is mogelijk, niet-overlappende opnameperiodes zijn niet bewezen.");
        for (var i = 0; i < unique.Count; i++)
            for (var j = i + 1; j < unique.Count; j++)
                if (unique[i].Bundle.FirstUtc < unique[j].Bundle.LastUtc && unique[j].Bundle.FirstUtc < unique[i].Bundle.LastUtc)
                    return Missing($"Opnameperiodes overlappen: {unique[i].Bundle.Name} en {unique[j].Bundle.Name}. Sluit een conflicterend pakket expliciet uit; gelijke waarden bewijzen geen duplicaat.");

        var metrics = unique.Select(item => item.Metric).ToArray();
        var denominator = metrics.Sum(metric => metric.Denominator);
        double? value;
        if (metrics[0].Aggregation is ComparisonAggregation.EventMean or ComparisonAggregation.EventP95)
        {
            if (metrics.Any(metric => metric.AcceptedEvents is null))
                return Missing("Geen volledige eventverdeling beschikbaar. P95-waarden worden nooit gemiddeld.");
            var events = metrics.SelectMany(metric => metric.AcceptedEvents!).Order().ToArray();
            denominator = events.Length;
            value = events.Length == 0 ? null : metrics[0].Aggregation == ComparisonAggregation.EventMean ? events.Average() : Percentile(events, .95);
        }
        else value = denominator > 0 ? metrics.Sum(metric => metric.Numerator) / denominator *
            (metrics[0].Aggregation == ComparisonAggregation.Percentage ? 100 : 1) : null;
        if (!double.IsFinite(denominator) || value.HasValue && !double.IsFinite(value.Value))
            return Missing("De gezamenlijke berekening valt buiten het ondersteunde getalbereik.");
        return new(value, unit, unique.Count, duplicates, denominator,
            value.HasValue ? $"{unique.Count} verenigbare pakket(ten); {duplicates} identieke bronset(s) tellen niet opnieuw mee. " +
                (metrics[0].Aggregation is ComparisonAggregation.EventMean or ComparisonAggregation.EventP95
                    ? "Berekend uit alle geaccepteerde events." : "Berekend uit de som van tellers en noemers; ontbrekende dekking telt niet mee.")
                : "Geen bruikbare dekking of geaccepteerde events.");
    }

    public static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        var position = (sorted.Count - 1) * fraction;
        var lower = (int)Math.Floor(position); var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}

/// <summary>Reads bounded known entries directly from the ZIP; samples and arbitrary archive paths are never extracted.</summary>
public static class AnalysisBundleComparisonReader
{
    private const int MaximumRows = 2_000_000;
    public static AnalysisComparisonBundle Read(string path, CancellationToken cancellationToken = default)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 100) throw new InvalidDataException("Het analysepakket bevat te veel onderdelen.");
        if (zip.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("Het analysepakket bevat dubbele onderdeel-namen.");
        using var manifestDocument = JsonDocument.Parse(ReadText(zip, "manifest.json", 1_048_576, true)!);
        var manifest = manifestDocument.RootElement;
        var schema = (int)(Number(manifest, "SchemaVersion") ?? 0);
        if (schema is not (2 or 3)) throw new InvalidDataException("Alleen analysepakketten schema 2 en 3 worden ondersteund.");
        var detailsEntry = Text(manifest, "AnalysisDetailsEntry");
        if (detailsEntry.Length > 0 && (detailsEntry != "analysis_details.json" || zip.GetEntry(detailsEntry) is null))
            throw new InvalidDataException("Het volledige analyseonderdeel ontbreekt of heeft een onbekende naam.");
        using var analysisDocument = AnalysisComparisonJsonProjection.Read(zip, detailsEntry.Length > 0 ? detailsEntry : "analyses.json", cancellationToken);
        using var signalsDocument = JsonDocument.Parse(ReadText(zip, "signals.json", 16 * 1024 * 1024, false) ?? "[]");
        using var sourcesDocument = JsonDocument.Parse(ReadText(zip, "sources.json", 16 * 1024 * 1024, false) ?? "[]");
        using var gapsDocument = JsonDocument.Parse(ReadText(zip, "gaps.json", 16 * 1024 * 1024, false) ?? "[]");
        using var channelGapsDocument = JsonDocument.Parse(ReadText(zip, "channel_gaps.json", 16 * 1024 * 1024, false) ?? "{}");
        using var breaksDocument = JsonDocument.Parse(ReadText(zip, "continuity_breaks.json", 16 * 1024 * 1024, false) ?? "[]");
        var globalGaps = ReadGaps(gapsDocument.RootElement);
        var channelGaps = channelGapsDocument.RootElement.ValueKind == JsonValueKind.Object
            ? channelGapsDocument.RootElement.EnumerateObject().ToDictionary(property => property.Name,
                property => ReadGaps(property.Value), StringComparer.OrdinalIgnoreCase)
            : throw new InvalidDataException("Ongeldige kanaalmeetgaten.");
        var continuity = Elements(breaksDocument.RootElement).Select(item =>
        {
            var before = Number(item, "BeforeTimestampNanoseconds") ?? throw new InvalidDataException("Ongeldige continuïteitsbreuk.");
            var after = Number(item, "AfterTimestampNanoseconds") ?? throw new InvalidDataException("Ongeldige continuïteitsbreuk.");
            return new MeasurementGap(Math.Min(before, after) / 1e9, Math.Max(before, after) / 1e9);
        }).ToArray();
        var analysis = analysisDocument.RootElement;
        var options = Property(manifest, "Options");
        var machine = Text(options, "MachineId");
        if (string.IsNullOrWhiteSpace(machine))
        {
            var sourcesForMachine = Elements(sourcesDocument.RootElement).ToArray();
            var machines = sourcesForMachine.Select(source => Text(source, "MachineId")).Distinct().ToArray();
            var allVerified = sourcesForMachine.Length > 0 && sourcesForMachine.All(source =>
                Text(source, "MachineAssignmentQuality") == "embedded-installation" ||
                Text(source, "MachineAssignmentQuality") == "verified-installation" &&
                !string.IsNullOrWhiteSpace(Text(source, "MachineAssignmentEvidence")));
            machine = allVerified && machines.Length == 1 && !string.IsNullOrWhiteSpace(machines[0]) ? machines[0] : "";
        }
        var method = Text(manifest, "MethodVersion") + "|" + Text(manifest, "DbcSha256") + "|" +
            (Number(options, "MaximumGapSeconds")?.ToString("R", CultureInfo.InvariantCulture) ?? "unknown");
        var metrics = new List<ComparisonMetric>();
        var signalDefinitions = Elements(signalsDocument.RootElement).Where(item => Number(item, "SignalId").HasValue)
            .ToDictionary(item => (int)Number(item, "SignalId")!.Value);
        var trends = new Dictionary<int, List<ComparisonTrendPoint>>();
        foreach (var row in ReadCsv(zip, "signal_minutes.csv", 128 * 1024 * 1024, cancellationToken))
        {
            var id = Integer(row, "signal_id");
            var point = new ComparisonTrendPoint(Required(row, "start_seconds"), Required(row, "end_seconds"),
                Required(row, "covered_seconds"), Required(row, "integral_value_seconds"));
            if (point.EndSeconds < point.StartSeconds || point.CoveredSeconds < 0 ||
                point.CoveredSeconds > point.EndSeconds - point.StartSeconds + 1e-6)
                throw new InvalidDataException("Ongeldige dekking in minuutstatistieken.");
            if (!trends.TryGetValue(id, out var list)) trends.Add(id, list = []);
            list.Add(point);
        }
        var start = double.PositiveInfinity; var end = double.NegativeInfinity;
        foreach (var row in ReadCsv(zip, "signal_statistics.csv", 64 * 1024 * 1024, cancellationToken))
        {
            var id = Integer(row, "signal_id");
            var unit = Field(row, "unit");
            var covered = Required(row, "covered_seconds"); var integral = Required(row, "integral_value_seconds");
            var first = Required(row, "first_seconds"); var last = Required(row, "last_seconds");
            if (last < first || covered < 0 || covered > last - first + 1e-6)
                throw new InvalidDataException("Ongeldige meetdekking in signaalstatistieken.");
            start = Math.Min(start, first); end = Math.Max(end, last);
            var stable = signalDefinitions.TryGetValue(id, out var definition) ? Text(definition, "StableSignalId") : "";
            // Legacy identity is used only for side-by-side matching, never to authorize aggregation.
            if (stable.Length == 0) stable = AnalysisBundleIdentity.Hash(string.Join("|", new[] { "channel", "frame_format", "extended", "frame_id", "message", "signal", "unit" }.Select(key => Field(row, key))));
            var identityJson = Property(definition, "Identity");
            var identity = identityJson.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<SignalIdentity>(identityJson.GetRawText(), AnalysisBundleExporter.JsonOptions) : null;
            // Match the same decoded identity across units for side-by-side inspection; unit and
            // the original stable ID still participate in compatibility, so they cannot be summed.
            var key = identity is null ? "signal:" + stable : SignalKey(identity);
            var label = $"{Field(row, "message")}.{Field(row, "signal")} [{Field(row, "channel")}] — tijdgemiddelde";
            var ordered = trends.TryGetValue(id, out var points) ? points.OrderBy(point => point.StartSeconds).ToArray() : [];
            if (ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.EndSeconds > pair.Second.StartSeconds))
                throw new InvalidDataException("Overlappende minuutstatistieken binnen één signaal.");
            MarkTrendBreaks(ordered, (channelGaps.TryGetValue(Field(row, "channel"), out var specific) ? specific : globalGaps)
                .Concat(continuity).OrderBy(gap => gap.StartSeconds).ToArray());
            metrics.Add(new(key, label, unit, ComparisonAggregation.WeightedMean, covered > 0 ? integral / covered : null,
                integral, covered, method + "|" + stable + "|" + unit, ordered, Minimum: Optional(row, "minimum"), Maximum: Optional(row, "maximum"))
                { DisplayTrend = PrepareDisplayTrend(ordered) });
        }
        AddModuleMetrics(analysis, method, metrics);
        if (Number(analysis, "StartSeconds") is { } declaredStart) start = declaredStart;
        if (Number(analysis, "EndSeconds") is { } declaredEnd) end = declaredEnd;
        if (!double.IsFinite(start)) start = 0;
        if (!double.IsFinite(end)) end = start;
        if (end < start) throw new InvalidDataException("Ongeldig tijdvenster in het analysepakket.");
        var notes = new List<string>();
        if (schema < 3) notes.Add("Ouder schema: afzonderlijke statistieken en trends; gezamenlijke berekeningen uitgeschakeld.");
        var clock = Text(manifest, "ClockQuality");
        if (clock != "verified") notes.Add("Loggerklok onbevestigd: relatieve tijdweergave; geen automatische kalendervergelijking.");
        var full = Text(manifest, "AnalysisScope") == "FullDataset" && Text(manifest, "StatisticsScope") == "FullDataset";
        if (!full) notes.Add("Analysebereiken zijn niet uniform als volledige dataset vastgelegd.");
        var declaredSources = Elements(Property(manifest, "SourceIds")).ToArray();
        var sources = declaredSources.Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var sourceIdentityComplete = Boolean(manifest, "SourceIdentityComplete") && sources.Length > 0 &&
            declaredSources.All(source => source.ValueKind == JsonValueKind.String) &&
            sources.All(source => source.Length == 71 && source.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                source[7..].All(Uri.IsHexDigit));
        if (!sourceIdentityComplete) notes.Add("Volledige SHA-256-bronidentiteit ontbreekt; gezamenlijke berekeningen uitgeschakeld.");
        var origin = DateTimeOffset.TryParse(Text(manifest, "StartTimeUtc"), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed) ? parsed : (DateTimeOffset?)null;
        if (origin.HasValue)
        {
            try { _ = origin.Value.AddSeconds(start); _ = origin.Value.AddSeconds(end); }
            catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Opnametijd valt buiten het ondersteunde datumbereik."); }
        }
        return new()
        {
            Path = System.IO.Path.GetFullPath(path), SchemaVersion = schema, MachineId = machine,
            LoggerId = Text(options, "LoggerId"), ClockQuality = clock, StartUtc = origin,
            StartSeconds = start, EndSeconds = end, SourceIds = sources,
            SourceIdentityComplete = sourceIdentityComplete,
            FullDataset = full, Completeness = Text(manifest, "Completeness"), Metrics = metrics, Notes = notes
        };
    }

    public static string SignalKey(SignalIdentity identity) => "signal:" + AnalysisBundleIdentity.SignalId(identity, string.Empty);

    private static void AddModuleMetrics(JsonElement analysis, string method, List<ComparisonMetric> metrics)
    {
        var profile = Property(analysis, "Profile");
        var usage = Property(analysis, "ActiveUsage");
        var value = Property(usage, "Value");
        var usageCompatibility = method + "|" + Canonical(Property(profile, "Usage")) + "|" + Text(usage, "MethodVersion");
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var (field, label, unit) in new[]
            {
                ("ActiveSeconds", "Actieve tijd", "s"), ("IdleSeconds", "Stationaire tijd", "s"),
                ("UnknownSeconds", "Onbekende gebruikstijd", "s"), ("DischargeSocPoints", "SOC-daling", "%-punt"),
                ("RiseSocPoints", "SOC-stijging", "%-punt"), ("ConfirmedCapacityKwh", "Bevestigde accucapaciteit", "kWh"),
                ("NetKwh", "Netto energie uit SOC", "kWh"), ("ActiveRuntimeHours", "Prognose actieve gebruiksduur", "h"),
                ("RemainingActiveHours", "Prognose resterende actieve gebruiksduur", "h")
            })
                metrics.Add(new("usage:" + field, label, unit, ComparisonAggregation.None, Number(value, field), 0, 0,
                    usageCompatibility, [], Note: Text(usage, "Explanation")));
        }
        if (Number(value, "ActivePercent") is { } activePercent)
        {
            var intervals = Elements(Property(value, "Intervals")).ToArray();
            var denominator = intervals.Where(interval => Text(interval, "State") is "Active" or "Idle" or "UnknownActivity")
                .Sum(interval => Math.Max(0, (Number(interval, "EndSeconds") ?? 0) - (Number(interval, "StartSeconds") ?? 0)));
            metrics.Add(new("usage:active", "Actief gebruik", "%", ComparisonAggregation.Percentage,
                activePercent, Number(value, "ActiveSeconds") ?? 0, denominator,
                method + "|" + Canonical(Property(profile, "Usage")) + "|" + Text(usage, "MethodVersion"), [], Note: Text(usage, "Explanation")));
        }
        foreach (var joystick in Elements(Property(analysis, "Joysticks")))
        {
            var subject = Text(joystick, "SubjectId"); var joystickValue = Property(joystick, "Value");
            var configuration = Elements(Property(profile, "Joysticks")).FirstOrDefault(item => Text(item, "Id") == subject);
            foreach (var band in Elements(Property(joystickValue, "AmplitudeBands")))
            {
                var name = Text(band, "Name"); var covered = Number(joystickValue, "CoveredSeconds") ?? 0;
                metrics.Add(new("joystick:" + subject + ":" + name, subject + " — " + name, "%", ComparisonAggregation.Percentage,
                    Number(band, "Percent"), Number(band, "Seconds") ?? 0, covered,
                    method + "|" + Canonical(configuration) + "|" + Text(joystick, "MethodVersion") + "|" +
                    Number(band, "Lower") + "|" + Number(band, "Upper"), [], Note: Text(joystick, "Explanation")));
            }
        }
        foreach (var actuator in Elements(Property(analysis, "Actuators")))
        {
            var subject = Text(actuator, "SubjectId");
            var configuration = Elements(Property(profile, "Actuators")).FirstOrDefault(item => Text(item, "Id") == subject);
            var trackingModule = Property(Property(actuator, "Value"), "Tracking");
            var tracking = Property(trackingModule, "Value");
            if (tracking.ValueKind == JsonValueKind.Object)
            {
                var moving = Number(tracking, "MovingSeconds") ?? 0;
                var meanError = Number(tracking, "MeanAbsoluteError");
                var unit = Text(trackingModule, "Unit");
                var effectiveResponse = Property(Property(Property(actuator, "Value"), "SetpointChange"), "Value");
                var commandThreshold = Number(effectiveResponse, "CommandThreshold");
                var responseThreshold = Number(effectiveResponse, "ResponseThreshold");
                var compatibility = method + "|" + Text(trackingModule, "MethodVersion") + "|" + Canonical(configuration) + "|" + unit + "|" +
                    commandThreshold?.ToString("R", CultureInfo.InvariantCulture) + "|" + responseThreshold?.ToString("R", CultureInfo.InvariantCulture);
                metrics.Add(new("tracking:" + subject + ":mean", subject + " — gemiddelde absolute volgfout", unit,
                    commandThreshold is > 0 && responseThreshold is > 0 && moving > 0 && meanError.HasValue && double.IsFinite(meanError.Value * moving)
                        ? ComparisonAggregation.WeightedMean : ComparisonAggregation.None,
                    meanError, (meanError ?? 0) * moving, moving, compatibility, [],
                    Maximum: Number(tracking, "MaximumAbsoluteError"), Note: Text(trackingModule, "Explanation")));
                metrics.Add(new("tracking:" + subject + ":p95", subject + " — P95 absolute volgfout", unit, ComparisonAggregation.None,
                    Number(tracking, "Percentile95AbsoluteError"), 0, 0, compatibility, [],
                    Note: "Geen volledige gewogen foutverdeling in dit pakket; P95 blijft per opname beschikbaar."));
            }
            foreach (var reference in new[] { "SetpointChange", "PositionRequest" })
            {
                var module = Property(Property(actuator, "Value"), reference); var response = Property(module, "Value");
                if (response.ValueKind != JsonValueKind.Object) continue;
                var eventsElement = Property(response, "Events");
                var events = eventsElement.ValueKind == JsonValueKind.Array ? Elements(eventsElement)
                    .Where(item => Text(item, "Outcome") == "Accepted").Select(item => Number(item, "DelaySeconds"))
                    .Where(delay => delay is >= 0).Select(delay => delay!.Value).Order().ToArray() : null;
                var statistics = Property(response, "AllStatistics");
                if (events is not null && (Number(statistics, "MatchedEventCount") != events.Length ||
                    Elements(eventsElement).Any(item => Text(item, "Outcome") == "Accepted" && Number(item, "DelaySeconds") is not >= 0)))
                    events = null; // An incomplete event distribution cannot establish a pooled percentile.
                var compatibility = method + "|" + Text(module, "MethodVersion") + "|" + reference + "|" + Canonical(configuration) +
                    "|" + Number(response, "CommandThreshold") + "|" + Number(response, "ResponseThreshold") + "|" + Number(response, "PositionTolerance");
                var mean = events is { Length: > 0 } ? events.Average() : Number(statistics, "MeanDelaySeconds");
                var p95 = events is { Length: > 0 } ? AnalysisBundleComparison.Percentile(events, .95) : Number(statistics, "Percentile95DelaySeconds");
                var denominator = events?.Length ?? Number(statistics, "MatchedEventCount") ?? 0;
                var key = "reaction:" + subject + ":" + reference;
                metrics.Add(new(key + ":mean", subject + " — " + reference + " — gemiddelde reactietijd", "s", ComparisonAggregation.EventMean,
                    mean, events?.Sum() ?? 0, denominator, compatibility, [], events, Note: Text(module, "Explanation")));
                metrics.Add(new(key + ":p95", subject + " — " + reference + " — P95 reactietijd", "s", ComparisonAggregation.EventP95,
                    p95, 0, denominator, compatibility, [], events, Note: Text(module, "Explanation")));
            }
        }
    }

    private static MeasurementGap[] ReadGaps(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Ongeldige meetgaten.");
        return Elements(element).Select(item =>
        {
            var start = Number(item, "StartSeconds") ?? throw new InvalidDataException("Ongeldig meetgat.");
            var end = Number(item, "EndSeconds") ?? throw new InvalidDataException("Ongeldig meetgat.");
            if (end < start) throw new InvalidDataException("Ongeldig meetgat.");
            return new MeasurementGap(start, end);
        }).ToArray();
    }

    private static void MarkTrendBreaks(ComparisonTrendPoint[] points, MeasurementGap[] gaps)
    {
        var gapIndex = 0;
        var previousUncertain = false;
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            while (gapIndex < gaps.Length && gaps[gapIndex].EndSeconds < point.StartSeconds) gapIndex++;
            // A minute mean cannot locate a missing subinterval. Isolate that bucket rather than
            // drawing a continuous line through a declared gap or a partially covered minute.
            var uncertain = point.CoveredSeconds < point.EndSeconds - point.StartSeconds - 1e-6 ||
                gapIndex < gaps.Length && gaps[gapIndex].StartSeconds <= point.EndSeconds;
            points[i] = point with { BreakBefore = uncertain || previousUncertain || point.BreakBefore };
            previousUncertain = uncertain;
        }
    }

    private static IReadOnlyList<ComparisonTrendPoint> PrepareDisplayTrend(ComparisonTrendPoint[] points)
    {
        if (points.Length == 0) return [];
        var x = new double[points.Length]; var y = new double[points.Length]; var gaps = new int[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            x[i] = i; y[i] = points[i].Value ?? double.NaN;
            if (i > 0) gaps[i] = gaps[i - 1] + (points[i].BreakBefore || points[i].StartSeconds > points[i - 1].EndSeconds ||
                !points[i].Value.HasValue || !points[i - 1].Value.HasValue ? 1 : 0);
        }
        var (indices, _) = Downsampling.MinMax(x, y, 2000);
        var result = new ComparisonTrendPoint[indices.Length]; var previous = 0;
        for (var i = 0; i < indices.Length; i++)
        {
            var index = (int)indices[i];
            result[i] = points[index] with { BreakBefore = points[index].BreakBefore || i > 0 && gaps[index] != gaps[previous] };
            previous = index;
        }
        return result;
    }

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().Where(property => property.Name is not ("Name" or "Evidence"))
            .OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => JsonSerializer.Serialize(property.Name) + ":" + Canonical(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.Undefined => "null",
        _ => value.GetRawText()
    };
    private static string? ReadText(ZipArchive zip, string name, long maximumBytes, bool required)
    {
        var entry = zip.Entries.SingleOrDefault(item => item.FullName == name);
        if (entry is null)
        {
            if (required) throw new InvalidDataException("Analyseonderdeel ontbreekt: " + name);
            return null;
        }
        if (entry.Length > maximumBytes) throw new InvalidDataException("Analyseonderdeel is te groot: " + name);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
    private static IEnumerable<Dictionary<string, string>> ReadCsv(ZipArchive zip, string name, long limit, CancellationToken token)
    {
        var text = ReadText(zip, name, limit, false);
        if (text is null) yield break;
        using var reader = new TextFieldParser(new StringReader(text)) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        reader.SetDelimiters(",");
        var header = reader.ReadFields();
        if (header is null) yield break;
        if (header.Distinct(StringComparer.Ordinal).Count() != header.Length) throw new InvalidDataException("Dubbele CSV-kolommen.");
        var count = 0;
        while (!reader.EndOfData)
        {
            token.ThrowIfCancellationRequested();
            if (++count > MaximumRows) throw new InvalidDataException("Te veel vergelijkingsregels.");
            var fields = reader.ReadFields();
            if (fields is null || fields.Length != header.Length) throw new InvalidDataException("Ongeldig aantal CSV-velden.");
            yield return header.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        }
    }
    private static string Field(IReadOnlyDictionary<string, string> row, string key) => row.TryGetValue(key, out var text) ? text : "";
    private static double? Optional(IReadOnlyDictionary<string, string> row, string key)
    {
        var text = Field(row, key);
        if (text.Length == 0) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value : throw new InvalidDataException("Ongeldig getal in " + key);
    }
    private static double Required(IReadOnlyDictionary<string, string> row, string key) => Optional(row, key) ?? throw new InvalidDataException("CSV-waarde ontbreekt: " + key);
    private static int Integer(IReadOnlyDictionary<string, string> row, string key) =>
        int.TryParse(Field(row, key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : throw new InvalidDataException("Ongeldige signaalidentiteit.");
    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string Text(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
    private static double? Number(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static bool Boolean(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.True;
    private static IEnumerable<JsonElement> Elements(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
}
