using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class AnalysisBundleComparisonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualStandardExporterRoundTripsItsStableSignalAndCoverage(bool completeSourceIdentity)
    {
        WithBundles((root, _) =>
        {
            var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 10, "Battery", "Voltage");
            var series = new SignalSeries(identity, [0L, 1_000_000_000L, 2_000_000_000L], [20d, 30d, 40d]);
            using var dataset = new CanDataset
            {
                RawFrames = Enumerable.Range(0, 3).Select(i => new RawCanFrame(i * 1_000_000_000L, 10, 1, [(byte)i], "DT", "1", false, i)).ToArray(),
                DecodedSamples = Enumerable.Range(0, 3).Select(i => new DecodedSignalSample(i * 1_000_000_000L, i, i + 1,
                    identity, 20 + i * 10, 20 + i * 10, "V")).ToArray(),
                SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries> { [identity] = series },
                SignalSeriesByLabel = new Dictionary<string, SignalSeries> { [identity.DisplayLabel] = series },
                SignalLabels = [identity.DisplayLabel], MessageSummaries = [], Diagnostics = new(0, 0, 0, 0, 0, "test"),
                DbcSha256 = "test-dbc", SourceLogSha256 = "test-source"
            };
            if (completeSourceIdentity)
                dataset.SourceFiles = [new("source.trc", "source.trc", null, null) { Sha256 = new string('A', 64) }];
            var path = Path.Combine(root, "actual.zip");
            AnalysisBundleExporter.ExportStandard(path, dataset, new() { Profile = new() { DbcSha256 = "test-dbc", MaximumGapSeconds = 5, MachineId = "M01" } });
            var bundle = Read(path);
            var signal = Assert.Single(bundle.Metrics, metric => metric.Key.StartsWith("signal:"));
            Assert.Equal(AnalysisBundleComparisonReader.SignalKey(identity), signal.Key);
            Assert.Equal(25, signal.Value);
            Assert.Equal(2, signal.Denominator);
            Assert.Equal(50, signal.Numerator);
            Assert.Single(signal.Trend);
            Assert.Equal(completeSourceIdentity, bundle.SourceIdentityComplete);
            if (completeSourceIdentity)
            {
                Assert.StartsWith("sha256:", Assert.Single(bundle.SourceIds));
                Assert.Equal(25, AnalysisBundleComparison.Aggregate([bundle], signal.Key).Value);
                using var archive = ZipFile.OpenRead(path);
                using var input = archive.GetEntry("signals.json")!.Open();
                using var signals = JsonDocument.Parse(input);
                Assert.Equal(0, Assert.Single(signals.RootElement.EnumerateArray()).GetProperty("SourceIndices")[0].GetInt32());
            }
        });
    }

    [Fact]
    public void CompatibleDisjointBundlesUseIntegralsAndCoverageAndDeduplicateExactSourceSets()
    {
        WithBundles((root, write) =>
        {
            var first = Read(write("first", ["A"], 0, 10, 20));
            var second = Read(write("second", ["B"], 60, 90, 900));
            var duplicate = Read(write("duplicate", ["A"], 0, 10, 20));
            var result = AnalysisBundleComparison.Aggregate([first, second, duplicate], "signal:stable");
            Assert.Equal(9.2, result.Value!.Value, 10);
            Assert.Equal(100, result.Denominator);
            Assert.Equal(2, result.IncludedBundles);
            Assert.Equal(1, result.DuplicateBundles);
            Assert.Equal(2, first.Metrics.Single().Trend[0].Value);
            Assert.Equal("Message.value, quoted [1] — tijdgemiddelde", first.Metrics.Single().Label);
        });
    }

    [Fact]
    public void PartialSourceOverlapAndOverlappingClockIntervalsBlockAggregationUntilExcluded()
    {
        WithBundles((_, write) =>
        {
            var first = Read(write("first", ["A", "B"], 0, 10, 20));
            var overlap = Read(write("partial", ["B", "C"], 100, 10, 20));
            var conflict = AnalysisBundleComparison.Aggregate([first, overlap], "signal:stable").Explanation;
            Assert.Contains("Gedeeltelijk", conflict);
            Assert.Contains("first.zip", conflict); Assert.Contains("partial.zip", conflict);
            var timeOverlap = Read(write("clock", ["D"], 5, 10, 20));
            conflict = AnalysisBundleComparison.Aggregate([first, timeOverlap], "signal:stable").Explanation;
            Assert.Contains("Opnameperiodes overlappen", conflict);
            Assert.Contains("first.zip", conflict); Assert.Contains("clock.zip", conflict);
            Assert.Equal(2, AnalysisBundleComparison.Aggregate([first], "signal:stable").Value);
        });
    }

    [Fact]
    public void DifferentMethodsOnIdenticalSourcesAreNotSilentlyChosenAsDuplicate()
    {
        WithBundles((root, write) =>
        {
            var first = Read(write("first", ["A"], 0, 10, 20));
            var changed = Read(Write(root, "changed", ["A"], 0, 10, 20, method: "changed-method"));
            var result = AnalysisBundleComparison.Aggregate([first, changed], "signal:stable");
            Assert.Null(result.Value);
            Assert.Contains("Niet verenigbaar", result.Explanation);
            Assert.Equal(2, changed.Metrics.Single().Value);
        });
    }

    [Fact]
    public void UnverifiedClocksAndLegacySchemaRemainReadableWithoutInventingAggregateOrCalendarAlignment()
    {
        WithBundles((root, write) =>
        {
            var verified = Read(write("first", ["A"], 0, 10, 20));
            var unknown = Read(Write(root, "unknown", ["B"], 60, 10, 20, clock: "unverified"));
            Assert.False(unknown.CanAlignUtc);
            Assert.Null(unknown.FirstUtc);
            Assert.NotEmpty(unknown.Metrics.Single().Trend);
            Assert.Null(AnalysisBundleComparison.Aggregate([verified, unknown], "signal:stable").Value);
            var legacy = Read(Write(root, "legacy", ["C"], 120, 10, 20, schema: 2));
            Assert.Equal(2, legacy.Metrics.Single().Value);
            Assert.Contains("exportschema", AnalysisBundleComparison.Aggregate([legacy], "signal:stable").Explanation);
        });
    }

    [Fact]
    public void ReactionP95IsRecomputedFromAcceptedEventsAndNeverAveragesSessionPercentiles()
    {
        WithBundles((root, _) =>
        {
            var first = Read(Write(root, "first", ["A"], 0, 10, 20, analysis: Reaction([1, 2], [999])));
            var second = Read(Write(root, "second", ["B"], 60, 10, 20, analysis: Reaction([10], [])));
            const string key = "reaction:actuator:SetpointChange:p95";
            var result = AnalysisBundleComparison.Aggregate([first, second], key);
            Assert.Equal(9.2, result.Value!.Value, 10);
            Assert.Equal(3, result.Denominator);
            var absent = Read(Write(root, "absent", ["C"], 120, 10, 20, analysis: Reaction(null, [])));
            Assert.Equal(4.5, absent.Metrics.Single(metric => metric.Key == key).Value);
            Assert.Null(AnalysisBundleComparison.Aggregate([first, absent], key).Value);
        });
    }

    [Fact]
    public void PercentagesCombineTheirTimeDenominatorsAndRequireMatchingCalibration()
    {
        WithBundles((root, _) =>
        {
            var first = Read(Write(root, "first", ["A"], 0, 10, 20, analysis: Joystick(1, 10, .1)));
            var second = Read(Write(root, "second", ["B"], 100, 90, 900, analysis: Joystick(81, 90, .1)));
            const string key = "joystick:stick:Neutraal";
            Assert.Equal(82, AnalysisBundleComparison.Aggregate([first, second], key).Value);
            var changed = Read(Write(root, "changed", ["C"], 200, 10, 20, analysis: Joystick(2, 10, .2)));
            Assert.Null(AnalysisBundleComparison.Aggregate([first, changed], key).Value);
        });
    }

    [Fact]
    public void DuplicateZipEntriesAndImpossibleCoverageAreRejectedWithoutExtractingFiles()
    {
        WithBundles((root, write) =>
        {
            var path = write("duplicate-entry", ["A"], 0, 10, 20);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                Put(zip, "manifest.json", "{}");
            Assert.Throws<InvalidDataException>(() => Read(path));
            var invalid = Write(root, "invalid", ["B"], 0, 10, 20, invalidCoverage: true);
            Assert.Throws<InvalidDataException>(() => Read(invalid));
            Assert.Equal(2, Directory.EnumerateFiles(root).Count());
        });
    }

    [Fact]
    public void LongTrendPreviewKeepsPeaksAndGapBreaksWhileRetainingOriginalMinuteRows()
    {
        WithBundles((root, _) =>
        {
            var path = Write(root, "long-trend", ["A"], 0, 1_200_000, 1_200_000, durationSeconds: 1_200_060);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("signal_minutes.csv")!.Delete();
                var text = new StringBuilder("signal_id,start_seconds,end_seconds,covered_seconds,integral_value_seconds\n");
                for (var i = 0; i < 20_000; i++)
                {
                    var start = (i < 11_000 ? i : i + 1) * 60;
                    text.AppendLine($"1,{start},{start + 60},60,{(i == 10_001 ? 30_000 : 60)}");
                }
                Put(zip, "signal_minutes.csv", text.ToString());
                Put(zip, "gaps.json", "[{\"StartSeconds\":600070,\"EndSeconds\":600080}]");
            }
            var metric = Assert.Single(Read(path).Metrics);
            Assert.Equal(20_000, metric.Trend.Count);
            Assert.InRange(metric.DisplayTrend!.Count, 1, 2000);
            Assert.Contains(metric.DisplayTrend, point => point.Value == 500);
            Assert.True(Assert.Single(metric.DisplayTrend, point => point.Value == 500).BreakBefore);
            Assert.Contains(metric.DisplayTrend, point => point.BreakBefore);
        });
    }

    [Theory]
    [InlineData("global")]
    [InlineData("channel")]
    [InlineData("partial")]
    [InlineData("continuity")]
    [InlineData("other-channel")]
    public void MinuteTrendsNeverBridgeSubMinuteGapsOrPartialCoverage(string kind)
    {
        WithBundles((root, _) =>
        {
            var path = Write(root, kind, ["A"], 0, 3, 6);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("signal_minutes.csv")!.Delete();
                Put(zip, "signal_minutes.csv", "signal_id,start_seconds,end_seconds,covered_seconds,integral_value_seconds\n" +
                    "1,0,1,1,2\n" + (kind == "partial" ? "1,1,2,0.8,1.6\n" : "1,1,2,1,2\n") + "1,2,3,1,2\n");
                const string gap = "[{\"StartSeconds\":0.4,\"EndSeconds\":0.6}]";
                if (kind is "global" or "other-channel") Put(zip, "gaps.json", gap);
                if (kind == "channel") Put(zip, "channel_gaps.json", "{\"1\":" + gap + "}");
                if (kind == "other-channel") Put(zip, "channel_gaps.json", "{\"1\":[],\"2\":" + gap + "}");
                if (kind == "continuity") Put(zip, "continuity_breaks.json",
                    "[{\"BeforeTimestampNanoseconds\":500000000,\"AfterTimestampNanoseconds\":500000000}]");
            }
            var metric = Assert.Single(Read(path).Metrics);
            var expected = kind == "other-channel" ? new[] { false, false, false }
                : kind == "partial" ? [false, true, true] : [true, true, false];
            Assert.Equal(expected, metric.Trend.Select(point => point.BreakBefore));
            Assert.Equal(expected, metric.DisplayTrend!.Select(point => point.BreakBefore));
            Assert.All(metric.Trend, point => Assert.Equal(2, point.Value));
            Assert.Equal(2, metric.Value); // Gap rendering must not rewrite the exported statistic.
        });
    }

    [Fact]
    public void UsageAndEnergyRemainSideBySideWhileTrackingMeansUseMovingTimeAndP95IsNeverAveraged()
    {
        WithBundles((root, _) =>
        {
            object Analysis(double mean, double moving, double? commandThreshold = 1) => new
            {
                Profile = new { Usage = new { CapacityConfirmed = true }, Actuators = new[] { new { Id = "actuator", PositionReferenceConfirmed = true } } },
                ActiveUsage = new { MethodVersion = "1", Explanation = "Confirmed capacity; measured SOC", Value = new
                {
                    ActiveSeconds = 8, IdleSeconds = 1, UnknownSeconds = 1, ActivePercent = 80,
                    Intervals = new[] { new { State = "Active", StartSeconds = 0, EndSeconds = 8 },
                        new { State = "Idle", StartSeconds = 8, EndSeconds = 9 }, new { State = "UnknownActivity", StartSeconds = 9, EndSeconds = 10 } },
                    DischargeSocPoints = 4, RiseSocPoints = 1, ConfirmedCapacityKwh = 15, NetKwh = .45,
                    ActiveRuntimeHours = 2, RemainingActiveHours = (double?)null
                } },
                Actuators = new[] { new { SubjectId = "actuator", Value = new
                {
                    SetpointChange = new { Value = new { CommandThreshold = commandThreshold, ResponseThreshold = 1 } },
                    Tracking = new
                {
                    MethodVersion = "1", Unit = "mm", Explanation = "Time weighted during movement", Value = new
                    { CoveredSeconds = 10, MovingSeconds = moving, MeanAbsoluteError = mean, Percentile95AbsoluteError = 12, MaximumAbsoluteError = 15 }
                } } } }
            };
            var first = Read(Write(root, "first", ["A"], 0, 10, 20, analysis: Analysis(2, 2)));
            var second = Read(Write(root, "second", ["B"], 60, 10, 20, analysis: Analysis(10, 8)));
            Assert.Equal(8, first.Metrics.Single(metric => metric.Key == "usage:ActiveSeconds").Value);
            Assert.Equal(15, first.Metrics.Single(metric => metric.Key == "usage:ConfirmedCapacityKwh").Value);
            Assert.Equal(.45, first.Metrics.Single(metric => metric.Key == "usage:NetKwh").Value);
            Assert.Null(first.Metrics.Single(metric => metric.Key == "usage:RemainingActiveHours").Value);
            foreach (var key in new[] { "usage:ActiveSeconds", "usage:NetKwh", "usage:ActiveRuntimeHours", "tracking:actuator:p95" })
            {
                var aggregate = AnalysisBundleComparison.Aggregate([first, second], key);
                Assert.Null(aggregate.Value); Assert.Contains("naast elkaar", aggregate.Explanation);
            }
            var trackingMean = AnalysisBundleComparison.Aggregate([first, second], "tracking:actuator:mean");
            Assert.Equal(8.4, trackingMean.Value!.Value, 8);
            Assert.Equal(10, trackingMean.Denominator);
            Assert.Equal("mm", trackingMean.Unit);
            var changed = Read(Write(root, "changed", ["C"], 120, 10, 20, analysis: Analysis(10, 8, 2)));
            var absent = Read(Write(root, "absent", ["D"], 180, 10, 20, analysis: Analysis(10, 8, null)));
            Assert.Null(AnalysisBundleComparison.Aggregate([first, changed], "tracking:actuator:mean").Value);
            Assert.Null(AnalysisBundleComparison.Aggregate([first, absent], "tracking:actuator:mean").Value);
            Assert.Equal(10, absent.Metrics.Single(metric => metric.Key == "tracking:actuator:mean").Value);
        });
    }

    [Fact]
    public void SameSignalWithDifferentUnitsStillMatchesForSideBySideButCannotAggregate()
    {
        WithBundles((root, _) =>
        {
            var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 10, "Battery", "Voltage");
            AnalysisComparisonBundle Bundle(string name, string unit, double start)
            {
                var path = Write(root, name, [name], start, 10, 20);
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                {
                    zip.GetEntry("signals.json")!.Delete();
                    Put(zip, "signals.json", JsonSerializer.Serialize(new[] { new { SignalId = 1,
                        StableSignalId = AnalysisBundleIdentity.SignalId(identity, unit), Identity = identity, Unit = unit } }, AnalysisBundleExporter.JsonOptions));
                    zip.GetEntry("signal_statistics.csv")!.Delete();
                    Put(zip, "signal_statistics.csv", "signal_id,channel,message,signal,unit,first_seconds,last_seconds,covered_seconds,integral_value_seconds\n" +
                        $"1,1,Battery,Voltage,{unit},0,10,10,20\n");
                }
                return Read(path);
            }
            var volts = Bundle("volts", "V", 0); var millivolts = Bundle("millivolts", "mV", 60);
            Assert.Equal(volts.Metrics.Single().Key, millivolts.Metrics.Single().Key);
            Assert.Null(AnalysisBundleComparison.Aggregate([volts, millivolts], volts.Metrics.Single().Key).Value);
        });
    }

    private static object Reaction(double[]? accepted, double[] rejected) => new
    {
        Profile = new { Actuators = new[] { new { Id = "actuator", PositionReferenceConfirmed = false, ResponseOptions = new { MaxDelaySeconds = 12 } } } },
        Actuators = new[] { new { SubjectId = "actuator", Value = new { SetpointChange = new
        {
            MethodVersion = "1", Explanation = "Accepted only", Value = new
            {
                CommandThreshold = 1, ResponseThreshold = 1,
                Events = accepted?.Select(delay => new { Outcome = "Accepted", DelaySeconds = delay })
                    .Concat(rejected.Select(delay => new { Outcome = "AlreadyMoving", DelaySeconds = delay })).ToArray(),
                AllStatistics = new { MatchedEventCount = accepted?.Length ?? 1, MeanDelaySeconds = 4, Percentile95DelaySeconds = 4.5 }
            }
        } } } }
    };
    private static object Joystick(double seconds, double covered, double neutral) => new
    {
        Profile = new { Joysticks = new[] { new { Id = "stick", CalibrationConfirmed = true, NeutralThreshold = neutral } } },
        Joysticks = new[] { new { SubjectId = "stick", MethodVersion = "1", Value = new
        {
            CoveredSeconds = covered, AmplitudeBands = new[] { new { Name = "Neutraal", Lower = 0, Upper = neutral, Seconds = seconds, Percent = 100 * seconds / covered } }
        } } }
    };
    [Fact]
    public void DeclaredCompleteIdentityWithMalformedSourceIdCannotAggregate()
    {
        WithBundles((root, _) =>
        {
            var bundle = Read(Write(root, "malformed", ["not-a-content-hash"], 0, 10, 20, malformedSourceIds: true));
            Assert.False(bundle.SourceIdentityComplete);
            Assert.NotEmpty(bundle.Metrics);
            Assert.Null(AnalysisBundleComparison.Aggregate([bundle], bundle.Metrics[0].Key).Value);
        });
    }

    [Fact]
    public void MachineFallbackRequiresOneVerifiedIdentityAcrossEverySource()
    {
        WithBundles((root, _) =>
        {
            var label = new { MachineId = "M01", MachineAssignmentQuality = "label", MachineAssignmentEvidence = "" };
            var verified = new { MachineId = "M01", MachineAssignmentQuality = "verified-installation", MachineAssignmentEvidence = "installation record" };
            var labelled = Read(Write(root, "label", ["A"], 0, 10, 20, machineId: "", sourceMetadata: new[] { label }));
            Assert.Empty(labelled.MachineId);
            Assert.Null(AnalysisBundleComparison.Aggregate([labelled], labelled.Metrics[0].Key).Value);
            var known = Read(Write(root, "verified", ["B"], 0, 10, 20, machineId: "", sourceMetadata: new[] { verified }));
            Assert.Equal("M01", known.MachineId);
            var mixed = Read(Write(root, "mixed", ["C", "D"], 0, 10, 20, machineId: "", sourceMetadata: new[] { verified, label }));
            Assert.Empty(mixed.MachineId);
        });
    }

    private static AnalysisComparisonBundle Read(string path) => AnalysisBundleComparisonReader.Read(path);

    [Fact]
    public void LargeLegacyEvidenceIsStreamedWithoutLosingExactReactionDistribution()
    {
        WithBundles((root, _) =>
        {
            var path = Write(root, "large-evidence", ["A"], 0, 10, 20);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("analyses.json")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("analyses.json").Open());
                var reaction = JsonSerializer.Serialize(Reaction([1, 2], [999]));
                writer.Write(reaction[..^1]);
                writer.Write(",\"Joysticks\":[{\"Value\":{\"InputEpisodes\":[");
                var row = "{\"StartSeconds\":0,\"Evidence\":\"" + new string('x', 1024) + "\"}";
                for (var i = 0; i < 70_000; i++) { if (i > 0) writer.Write(','); writer.Write(row); }
                writer.Write("]}}]}");
            }
            using (var zip = ZipFile.OpenRead(path)) Assert.True(zip.GetEntry("analyses.json")!.Length > 64 * 1024 * 1024);
            var bundle = Read(path);
            const string key = "reaction:actuator:SetpointChange:p95";
            Assert.Equal(1.95, AnalysisBundleComparison.Aggregate([bundle], key).Value!.Value, 10);
            Assert.Equal(new[] { 1d, 2d }, bundle.Metrics.Single(metric => metric.Key == key).AcceptedEvents);
        });
    }

    [Fact]
    public void ProjectionRejectsOversizedTokensEvenInSkippedEvidence()
    {
        WithBundles((root, _) =>
        {
            var path = Write(root, "large-token", ["A"], 0, 10, 20);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("analyses.json")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("analyses.json").Open());
                writer.Write("{\"Quality\":\""); writer.Write(new string('x', 2 * 1024 * 1024)); writer.Write("\"}");
            }
            Assert.Throws<InvalidDataException>(() => Read(path));
        });
    }

    private delegate string WriteBundle(string name, string[] sources, double originSeconds, double covered, double integral);
    private static void WithBundles(Action<string, WriteBundle> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "canalyser-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { body(root, (name, sources, start, covered, integral) => Write(root, name, sources, start, covered, integral)); }
        finally { Directory.Delete(root, true); }
    }
    private static string Write(string root, string name, string[] sources, double originSeconds, double covered, double integral,
        string method = "bundle-3", string clock = "verified", int schema = 3, object? analysis = null, bool invalidCoverage = false,
        double? durationSeconds = null, bool malformedSourceIds = false, string machineId = "M01", object? sourceMetadata = null)
    {
        var path = Path.Combine(root, name + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Put(zip, "manifest.json", JsonSerializer.Serialize(new
        {
            SchemaVersion = schema, MethodVersion = method, DbcSha256 = "DBC", Completeness = "Complete",
            StartTimeUtc = DateTimeOffset.Parse("2026-10-27T08:00:00Z").AddSeconds(originSeconds), ClockQuality = clock,
            AnalysisScope = "FullDataset", StatisticsScope = "FullDataset",
            SourceIds = malformedSourceIds ? sources : sources.Select(source => "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source)))).ToArray(),
            SourceIdentityComplete = true,
            Options = new { MachineId = machineId, LoggerId = "LOGGER", MaximumGapSeconds = 5 }
        }));
        if (sourceMetadata is not null) Put(zip, "sources.json", JsonSerializer.Serialize(sourceMetadata));
        Put(zip, "analyses.json", JsonSerializer.Serialize(analysis ?? new { }));
        Put(zip, "signals.json", "[{\"SignalId\":1,\"StableSignalId\":\"stable\",\"Unit\":\"V\"}]");
        string N(double number) => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        Put(zip, "signal_statistics.csv", "signal_id,channel,message,signal,unit,first_seconds,last_seconds,covered_seconds,integral_value_seconds,minimum,maximum\n" +
            $"1,1,Message,\"value, quoted\",V,0,{N(durationSeconds ?? covered)},{N(invalidCoverage ? covered + 1 : covered)},{N(integral)},0,10\n");
        // A two-minute bucket is valid here: the reader validates coverage and time ranges, not presentation resolution.
        Put(zip, "signal_minutes.csv", "signal_id,start_seconds,end_seconds,covered_seconds,integral_value_seconds\n" +
            $"1,0,{N(Math.Max(60, covered))},{N(covered)},{N(integral)}\n");
        return path;
    }
    private static void Put(ZipArchive zip, string name, string contents)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }
}
