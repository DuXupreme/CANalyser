using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Analysis;

public sealed record AnalysisExportOptions(string MachineId, string LoggerId, string BatteryId,
    double MaximumGapSeconds = 5, bool IncludeSamples = false);

/// <summary>Versioned, local export. Reads the sample store once, without materializing signal arrays.</summary>
public static class AnalysisBundleExporter
{
    public static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private static readonly JsonSerializerOptions CompactOptions = new(JsonOptions) { WriteIndented = false };
    private static readonly JsonSerializerOptions SummaryOptions = CreateSummaryOptions();
    private static JsonSerializerOptions CreateSummaryOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            string[] omitted = info.Type == typeof(MotionResponseResult) ? ["Events"]
                : info.Type == typeof(JoystickUsageSummary) ? ["InputEpisodes", "FullHolds"]
                : info.Type == typeof(UsageAnalysisSummary) ? ["Intervals"]
                : info.Type == typeof(BatteryConfigurationResult) ? ["Intervals", "ObservedConfigurations"] : [];
            foreach (var property in info.Properties.Where(property => omitted.Contains(property.Name)))
                property.ShouldSerialize = (_, _) => false;
        });
        return new(CompactOptions) { TypeInfoResolver = resolver };
    }
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new FiniteDoubleConverter());
        return options;
    }

    public static void Export(string path, CanDataset dataset, AnalysisExportOptions options,
        JsonElement analyses, IReadOnlyList<string> sourceEntries, CancellationToken cancellationToken = default)
        => ExportCore(path, dataset, options, analyses, sourceEntries, false, cancellationToken);

    public static StandardAnalysisRun ExportStandard(string path, CanDataset dataset, AnalysisRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Scope != "FullDataset" || request.Modules != AnalysisModules.All || request.StartSeconds.HasValue || request.EndSeconds.HasValue)
            throw new ArgumentException("Een machine-export vereist de volledige dataset en het volledige standaardanalyseplan.", nameof(request));
        using var lease = dataset.AcquireReadLease();
        var run = new AnalysisRunService().Analyze(dataset, request, cancellationToken);
        var loggers = dataset.SourceFiles.Select(source => source.Logger).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        var options = new AnalysisExportOptions(run.Profile.MachineId ?? string.Empty,
            loggers.Length == 1 ? loggers[0]! : string.Empty, string.Empty, run.Profile.MaximumGapSeconds, request.IncludeSamples);
        ExportCore(path, dataset, options, JsonSerializer.SerializeToElement(run, SummaryOptions),
            dataset.SourceFiles.Select(source => source.Name).ToArray(), true, cancellationToken, run);
        return run;
    }

    private static void ExportCore(string path, CanDataset dataset, AnalysisExportOptions options,
        JsonElement analyses, IReadOnlyList<string> sourceEntries, bool standard, CancellationToken cancellationToken,
        StandardAnalysisRun? fullDetails = null)
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
                    SchemaVersion = standard ? 3 : 2, MethodVersion = standard ? "canalyser-analysis-bundle-3" : "canalyser-analysis-bundle-2",
                    ExportedAtUtc = DateTimeOffset.UtcNow, dataset.ApplicationVersion,
                    dataset.SourceLogSha256, dataset.DbcSha256, dataset.Completeness,
                    dataset.StartTimeUtc, dataset.FirstRecordOffsetNanoseconds, dataset.ClockQuality,
                    TimestampMeaning = "UTC-formatted logger clock, not automatically verified UTC. Upload timestamps are separate source metadata. No implicit clock correction.",
                    dataset.RawCount, dataset.SignalCount, Options = options, SourceEntries = sourceEntries,
                    StatisticsScope = standard ? "FullDataset" : "Complete imported dataset; analysis-specific selections are in analyses.json",
                    AnalysisScope = standard ? "FullDataset" : "ConfiguredSelections",
                    AnalysisDetailsEntry = standard ? "analysis_details.json" : null,
                    AnalysisContent = standard ? "analyses.json and analysis_values.csv are summaries; full events, episodes and intervals are preserved in analysis_details.json." : "Legacy analysis payload",
                    SourceIds = AnalysisBundleIdentity.SourceIds(dataset),
                    SourceIdentityComplete = dataset.SourceFiles.Count > 0 && dataset.SourceFiles.All(source => OnlineLogArchiveManifestCodec.IsSha256(source.Sha256)),
                    MissingResults = "null / empty means unavailable, not zero; see analysis status and settings",
                    Aggregation = "Deduplicate sources and reject overlapping machine/logger UTC intervals before summing. Sum integral / sum covered_seconds for weighted means. Never average session percentiles.",
                    TimeWeighting = "Previous finite value held until next finite sample, only for strictly increasing intervals <= MaximumGapSeconds. Intervals crossing a declared import gap are excluded. No extrapolation after last sample.",
                    Units = "signal_statistics unit comes from decoded DBC; integral is unit*seconds; time is seconds; minute buckets use logger-relative seconds plus UTC origin",
                    DisplayMetrics = "Human-readable only; do not parse localized values for aggregation",
                    SourceIdentity = "SourceIds are per-file SHA-256 identities, independent of archive packaging. Unknown machine/installation identity remains unknown. SourceFileIndex maps original samples to source_registry.json."
                });
                WriteJson(zip, "analyses.json", analyses, CompactOptions);
                if (fullDetails is not null) WriteJson(zip, "analysis_details.json", fullDetails, CompactOptions, cancellationToken);
                WriteJson(zip, "gaps.json", dataset.ImportReport?.Gaps ?? []);
                WriteJson(zip, "exclusions.json", dataset.ImportReport?.Exclusions ?? []);
                WriteJson(zip, "continuity_breaks.json", dataset.ImportReport?.ContinuityBreaks ?? []);
                WriteJson(zip, "channel_gaps.json", dataset.ImportReport?.GapsByChannel ?? new Dictionary<string, IReadOnlyList<MeasurementGap>>());
                WriteJson(zip, "sources.json", dataset.SourceFiles);
                WriteJson(zip, "source_registry.json", dataset.SourceFiles.Select((source, index) => new
                {
                    SourceIndex = index, SourceId = AnalysisBundleIdentity.SourceId(source), source.Name, source.Sha256, source.SizeBytes,
                    source.MachineId, source.InstallationId, source.MachineAssignmentQuality, source.RecordedAt,
                    source.UploadedAtUtc, source.ClockQuality, source.ObjectKey, source.ObjectVersion
                }).ToArray());
                WriteJson(zip, "logger_diagnostics.json", dataset.LoggerDiagnostics);
                WriteJson(zip, "messages.json", dataset.MessageSummaries);
                using (var flat = Writer(zip, "analysis_values.csv"))
                {
                    Row(flat, "path", "type", "value");
                    Flatten(flat, analyses, "analyses");
                }
                using (var readme = Writer(zip, "README.txt"))
                    readme.Write("CSV: UTF-8, comma delimiter, invariant decimal point, quoted fields. Empty numeric field means unknown.\n" +
                        "signal_statistics.csv covers every decoded signal; signal_minutes.csv contains time integrals per minute for trends.\n" +
                        "analyses.json and analysis_values.csv contain results, settings, units, coverage and availability notes. Schema 3 always uses the full dataset and the persisted standard analysis profile.\n" +
                        "analysis_details.json retains complete event, episode, usage and battery interval evidence; these variable-length collections are omitted from the compact analyses.json and flattened CSV summary.\n" +
                        "For means across exports use SUM(integral_value_seconds)/SUM(covered_seconds). Check units, calibration, IDs, coverage and PARTIAL status.\n" +
                        "Deduplicate source entries and reject overlapping time ranges before aggregation. No automatic merge is performed.\n" +
                        "Battery estimates from SOC are estimates, not measured capacity degradation. Histograms can only be combined with identical bins/reference.\n" +
                        "Optional samples.csv preserves exact logger-relative nanoseconds for later reanalysis; UTC-formatted columns retain the raw logger clock, not proven accuracy.\n" +
                        "sources.json and source_registry.json retain per-file hashes, original sample provenance, raw recording time, separate server receipt time, installation identity and clock quality.\n" +
                        "exclusions.json and continuity_breaks.json retain intentionally omitted files and explicit segment boundaries. Missing durations are not invented.\n" +
                        "channel_gaps.json contains channel-specific missing coverage; logger_diagnostics.json is recorded clock/storage evidence, not machine use or a live upload queue.\n");
                var states = new Dictionary<SignalIdentity, State>();
                var channelGaps = new Dictionary<string, MeasurementGap[]>(StringComparer.Ordinal);
                using var minuteSpool = Spool(temporary + ".minutes");
                using var minutes = new StreamWriter(minuteSpool, new UTF8Encoding(false), 65536, leaveOpen: true);
                Row(minutes, "signal_id", "start_seconds", "end_seconds", "start_utc", "covered_seconds", "integral_value_seconds", "time_weighted_mean");
                using var sampleSpool = options.IncludeSamples ? Spool(temporary + ".samples") : null;
                using var samples = sampleSpool is null ? null : new StreamWriter(sampleSpool, new UTF8Encoding(false), 65536, leaveOpen: true);
                if (samples is not null) Row(samples, "signal_id", "timestamp_ns", "timestamp_utc", "value", "quality", "frame_index", "source_index", "source_frame_index");
                foreach (var sample in dataset.DecodedSamples)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!states.TryGetValue(sample.Identity, out var state))
                    {
                        if (!channelGaps.TryGetValue(sample.Channel, out var gaps))
                        {
                            gaps = MeasurementGapSelection.ForChannel(dataset.ImportReport, sample.Channel)
                                .OrderBy(gap => gap.StartSeconds).ThenBy(gap => gap.EndSeconds).ToArray();
                            channelGaps.Add(sample.Channel, gaps);
                        }
                        state = new State(states.Count + 1, sample.Unit, gaps);
                        states.Add(sample.Identity, state);
                    }
                    if (!string.Equals(state.Unit, sample.Unit, StringComparison.Ordinal))
                        throw new InvalidDataException("Eenheid wisselt binnen hetzelfde signaal; export afgebroken.");
                    var sourceIndex = sample.SourceFileIndex >= 0 ? sample.SourceFileIndex : dataset.SourceFiles.Count == 1 ? 0 : -1;
                    var sourceFrame = sample.SourceFrameIndex >= 0 ? sample.SourceFrameIndex : dataset.SourceFiles.Count == 1 ? sample.FrameIndex : -1;
                    if (samples is not null) Row(samples, state.Id, sample.TimestampNanoseconds,
                        Utc(dataset, sample.TimestampNanoseconds), sample.Value, sample.Quality, sample.FrameIndex,
                        sourceIndex >= 0 ? sourceIndex : null,
                        sourceFrame >= 0 ? sourceFrame : null);
                    if (sourceIndex >= 0) state.SourceIndices.Add(sourceIndex);
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
                        var gaps = state.Gaps;
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
                WriteJson(zip, "signals.json", states.Select(pair => new
                {
                    SignalId = pair.Value.Id, StableSignalId = AnalysisBundleIdentity.SignalId(pair.Key, pair.Value.Unit),
                    Identity = pair.Key, pair.Value.Unit, SourceIndices = pair.Value.SourceIndices.Order().ToArray()
                }).OrderBy(item => item.SignalId).ToArray());
                CopySpool(zip, "signal_minutes.csv", minuteSpool, cancellationToken);
                if (samples is not null && sampleSpool is not null)
                {
                    samples.Flush();
                    CopySpool(zip, "samples.csv", sampleSpool, cancellationToken);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream Spool(string path) => new(path, FileMode.CreateNew, FileAccess.ReadWrite,
        FileShare.None, 65536, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    private static void CopySpool(ZipArchive zip, string name, Stream spool, CancellationToken cancellationToken)
    {
        spool.Position = 0;
        using var target = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        var buffer = new byte[65536];
        int read;
        while ((read = spool.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Write(buffer, 0, read);
        }
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
    private static void WriteJson(ZipArchive zip, string name, object value, JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        using var stream = new CancellationWriteStream(entry, cancellationToken);
        JsonSerializer.Serialize(stream, value, options ?? JsonOptions);
    }

    private sealed class CancellationWriteStream(Stream inner, CancellationToken token) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { token.ThrowIfCancellationRequested(); inner.Flush(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { token.ThrowIfCancellationRequested(); inner.Write(buffer); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
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
    private sealed class State(int id, string unit, MeasurementGap[] gaps)
    {
        public int Id = id; public string Unit = unit;
        public MeasurementGap[] Gaps = gaps;
        public long Count, FiniteCount, NonIncreasing;
        public double Minimum = double.PositiveInfinity, Maximum = double.NegativeInfinity;
        public double First = double.PositiveInfinity, Last = double.NegativeInfinity;
        public double Mean, M2, Covered, Integral, MinuteCovered, MinuteIntegral;
        public double MinuteStart = double.NaN;
        public int GapIndex;
        public HashSet<int> SourceIndices = [];
        public DecodedSignalSample? Previous;
    }
    private sealed class FiniteDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        { if (double.IsFinite(value)) writer.WriteNumberValue(value); else writer.WriteNullValue(); }
    }
}
