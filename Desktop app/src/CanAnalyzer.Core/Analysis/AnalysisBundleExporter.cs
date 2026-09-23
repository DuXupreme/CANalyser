using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Analysis;

public sealed record AnalysisExportOptions(string MachineId, string LoggerId, string BatteryId,
    double MaximumGapSeconds = 5, bool IncludeSamples = false);

/// <summary>Versioned, local export. Reads the sample store once, without materializing signal arrays.</summary>
public static class AnalysisBundleExporter
{
    public static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new FiniteDoubleConverter());
        return options;
    }

    public static void Export(string path, CanDataset dataset, AnalysisExportOptions options,
        JsonElement analyses, IReadOnlyList<string> sourceEntries, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(options.MaximumGapSeconds) || options.MaximumGapSeconds <= 0)
            throw new ArgumentException("Maximale sampleafstand moet positief zijn.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                WriteJson(zip, "manifest.json", new
                {
                    SchemaVersion = 1, MethodVersion = "canalyser-analysis-bundle-1",
                    ExportedAtUtc = DateTimeOffset.UtcNow, dataset.ApplicationVersion,
                    dataset.SourceLogSha256, dataset.DbcSha256, dataset.Completeness,
                    dataset.StartTimeUtc, dataset.FirstRecordOffsetNanoseconds,
                    dataset.RawCount, dataset.SignalCount, Options = options, SourceEntries = sourceEntries,
                    StatisticsScope = "Complete imported dataset; analysis-specific selections are in analyses.json",
                    MissingResults = "null / empty means unavailable, not zero; see analysis status and settings",
                    Aggregation = "Deduplicate sources and reject overlapping machine/logger UTC intervals before summing. Sum integral / sum covered_seconds for weighted means. Never average session percentiles.",
                    TimeWeighting = "Previous finite value held until next finite sample, only for strictly increasing intervals <= MaximumGapSeconds. Intervals crossing a declared import gap are excluded. No extrapolation after last sample.",
                    Units = "signal_statistics unit comes from decoded DBC; integral is unit*seconds; time is seconds; minute buckets use logger-relative seconds plus UTC origin",
                    DisplayMetrics = "Human-readable only; do not parse localized values for aggregation",
                    SourceIdentity = "Archive entry names identify logger/session when present. Unknown machine/battery IDs must be supplied by the operator. Source hash detects identical archives, not differently packaged overlapping logs."
                });
                WriteJson(zip, "analyses.json", analyses);
                WriteJson(zip, "gaps.json", dataset.ImportReport?.Gaps ?? []);
                WriteJson(zip, "messages.json", dataset.MessageSummaries);
                using (var flat = Writer(zip, "analysis_values.csv"))
                {
                    Row(flat, "path", "type", "value");
                    Flatten(flat, analyses, "analyses");
                }
                using (var readme = Writer(zip, "README.txt"))
                    readme.Write("CSV: UTF-8, comma delimiter, invariant decimal point, quoted fields. Empty numeric field means unknown.\n" +
                        "signal_statistics.csv covers every decoded signal; signal_minutes.csv contains time integrals per minute for trends.\n" +
                        "analyses.json and analysis_values.csv contain current configured analyses, numeric results, distributions, settings and availability notes.\n" +
                        "For means across exports use SUM(integral_value_seconds)/SUM(covered_seconds). Check units, calibration, IDs, coverage and PARTIAL status.\n" +
                        "Deduplicate source entries and reject overlapping time ranges before aggregation. No automatic merge is performed.\n" +
                        "Battery estimates from SOC are estimates, not measured capacity degradation. Histograms can only be combined with identical bins/reference.\n" +
                        "Optional samples.csv preserves exact logger-relative nanoseconds for later reanalysis; UTC is blank when unknown.\n");
                var states = new Dictionary<SignalIdentity, State>();
                var gaps = dataset.ImportReport?.Gaps.OrderBy(g => g.StartSeconds).ToArray() ?? [];
                using var minuteSpool = Spool(temporary + ".minutes");
                using var minutes = new StreamWriter(minuteSpool, new UTF8Encoding(false), 65536, leaveOpen: true);
                Row(minutes, "signal_id", "start_seconds", "end_seconds", "start_utc", "covered_seconds", "integral_value_seconds", "time_weighted_mean");
                using var sampleSpool = options.IncludeSamples ? Spool(temporary + ".samples") : null;
                using var samples = sampleSpool is null ? null : new StreamWriter(sampleSpool, new UTF8Encoding(false), 65536, leaveOpen: true);
                if (samples is not null) Row(samples, "signal_id", "timestamp_ns", "timestamp_utc", "value", "quality", "frame_index");
                foreach (var sample in dataset.DecodedSamples)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!states.TryGetValue(sample.Identity, out var state))
                    {
                        state = new State(states.Count + 1, sample.Unit);
                        states.Add(sample.Identity, state);
                    }
                    if (!string.Equals(state.Unit, sample.Unit, StringComparison.Ordinal))
                        throw new InvalidDataException("Eenheid wisselt binnen hetzelfde signaal; export afgebroken.");
                    if (samples is not null) Row(samples, state.Id, sample.TimestampNanoseconds,
                        Utc(dataset, sample.TimestampNanoseconds), sample.Value, sample.Quality, sample.FrameIndex);
                    var t = sample.TimeSeconds;
                    state.Count++;
                    if (double.IsFinite(sample.Value))
                    {
                        state.FiniteCount++;
                        state.Minimum = Math.Min(state.Minimum, sample.Value);
                        state.Maximum = Math.Max(state.Maximum, sample.Value);
                        var delta = sample.Value - state.Mean;
                        state.Mean += delta / state.FiniteCount;
                        state.M2 += delta * (sample.Value - state.Mean);
                    }
                    state.First = Math.Min(state.First, t); state.Last = Math.Max(state.Last, t);
                    if (state.Previous is { } previous)
                    {
                        var dt = t - previous.TimeSeconds;
                        if (dt <= 0) state.NonIncreasing++;
                        while (state.GapIndex < gaps.Length && gaps[state.GapIndex].EndSeconds <= previous.TimeSeconds) state.GapIndex++;
                        var crossesGap = state.GapIndex < gaps.Length && gaps[state.GapIndex].StartSeconds < t;
                        if (dt > 0 && dt <= options.MaximumGapSeconds && !crossesGap &&
                            double.IsFinite(previous.Value) && double.IsFinite(sample.Value))
                        {
                            state.Covered += dt; state.Integral += previous.Value * dt;
                            var cursor = previous.TimeSeconds;
                            while (cursor < t)
                            {
                                var bucket = Math.Floor(cursor / 60) * 60;
                                if (state.MinuteStart != bucket)
                                {
                                    FlushMinute(minutes, dataset, state);
                                    state.MinuteStart = bucket;
                                }
                                var finish = Math.Min(t, bucket + 60);
                                state.MinuteCovered += finish - cursor;
                                state.MinuteIntegral += previous.Value * (finish - cursor);
                                cursor = finish;
                            }
                        }
                    }
                    // Reject unsorted input rather than publish overlapping time-weighted statistics.
                    if (state.Previous is not null && sample.TimestampNanoseconds < state.Previous.TimestampNanoseconds)
                        throw new InvalidDataException("Niet-oplopende signaaltijd; export afgebroken om dubbeltelling te voorkomen.");
                    state.Previous = sample;
                }
                using (var statistics = Writer(zip, "signal_statistics.csv"))
                {
                Row(statistics, "signal_id", "channel", "frame_format", "extended", "frame_id", "message", "signal", "unit",
                    "count", "finite_count", "first_seconds", "last_seconds", "first_utc", "last_utc", "minimum", "maximum",
                    "sample_mean", "sample_m2", "covered_seconds", "integral_value_seconds", "time_weighted_mean", "non_increasing_intervals");
                foreach (var (identity, state) in states)
                {
                    FlushMinute(minutes, dataset, state);
                    Row(statistics, state.Id, identity.Channel, identity.FrameFormat, identity.IsExtended, identity.FrameId,
                        identity.MessageName, identity.SignalName, state.Unit, state.Count, state.FiniteCount, state.First, state.Last,
                        UtcSeconds(dataset, state.First), UtcSeconds(dataset, state.Last), state.Minimum, state.Maximum,
                        state.FiniteCount > 0 ? state.Mean : null, state.FiniteCount > 0 ? state.M2 : null,
                        state.Covered, state.Integral, state.Covered > 0 ? state.Integral / state.Covered : null, state.NonIncreasing);
                }
                }
                minutes.Flush();
                CopySpool(zip, "signal_minutes.csv", minuteSpool);
                if (samples is not null && sampleSpool is not null)
                {
                    samples.Flush();
                    CopySpool(zip, "samples.csv", sampleSpool);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream Spool(string path) => new(path, FileMode.CreateNew, FileAccess.ReadWrite,
        FileShare.None, 65536, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    private static void CopySpool(ZipArchive zip, string name, Stream spool)
    {
        spool.Position = 0;
        using var target = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        spool.CopyTo(target);
    }
    private static void FlushMinute(TextWriter writer, CanDataset dataset, State state)
    {
        if (state.MinuteCovered > 0) Row(writer, state.Id, state.MinuteStart, state.MinuteStart + 60,
            UtcSeconds(dataset, state.MinuteStart), state.MinuteCovered, state.MinuteIntegral, state.MinuteIntegral / state.MinuteCovered);
        state.MinuteCovered = state.MinuteIntegral = 0;
    }
    private static string? UtcSeconds(CanDataset dataset, double seconds) =>
        MeasurementTimestamp.TrySecondsToNanoseconds(seconds, out var ns) ? Utc(dataset, ns) : null;
    private static string? Utc(CanDataset dataset, long ns) => dataset.StartTimeUtc.HasValue
        ? MeasurementTimestamp.FormatUtcIso8601(dataset.StartTimeUtc.Value, ns) : null;
    private static StreamWriter Writer(ZipArchive zip, string name) => new(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
    private static void WriteJson(ZipArchive zip, string name, object value)
    { using var writer = Writer(zip, name); writer.Write(JsonSerializer.Serialize(value, JsonOptions)); }
    private static void Flatten(TextWriter writer, JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) Flatten(writer, property.Value, path + "." + property.Name);
        else if (value.ValueKind == JsonValueKind.Array)
        { var index = 0; foreach (var item in value.EnumerateArray()) Flatten(writer, item, path + "[" + index++ + "]"); }
        else Row(writer, path, value.ValueKind, value.ValueKind == JsonValueKind.Null ? null : value.ToString());
    }
    private static void Row(TextWriter writer, params object?[] values) => writer.WriteLine(string.Join(",", values.Select(value =>
    {
        var text = value switch { null => "", double d when !double.IsFinite(d) => "", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    })));
    private sealed class State(int id, string unit)
    {
        public int Id = id; public string Unit = unit;
        public long Count, FiniteCount, NonIncreasing;
        public double Minimum = double.PositiveInfinity, Maximum = double.NegativeInfinity;
        public double First = double.PositiveInfinity, Last = double.NegativeInfinity;
        public double Mean, M2, Covered, Integral, MinuteCovered, MinuteIntegral;
        public double MinuteStart = double.NaN;
        public int GapIndex;
        public DecodedSignalSample? Previous;
    }
    private sealed class FiniteDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        { if (double.IsFinite(value)) writer.WriteNumberValue(value); else writer.WriteNullValue(); }
    }
}
