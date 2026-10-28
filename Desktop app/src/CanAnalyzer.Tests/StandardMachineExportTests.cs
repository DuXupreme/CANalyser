using System.IO.Compression;
using System.Text.Json;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Storage;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class StandardMachineExportTests
{
    [Fact]
    public void StandardExportRejectsTemporaryScopeOrIncompletePlanBeforeWriting()
    {
        using var data = Dataset();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        Assert.Throws<ArgumentException>(() => AnalysisBundleExporter.ExportStandard(path, data,
            new() { Scope = "TimeWindow", StartSeconds = 0, EndSeconds = 1 }));
        Assert.Throws<ArgumentException>(() => AnalysisBundleExporter.ExportStandard(path, data,
            new() { Modules = AnalysisModules.Joystick }));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CancelledStandardExportPreservesExistingDestination()
    {
        using var data = Dataset();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try
        {
            File.WriteAllText(path, "previous export");
            Assert.ThrowsAny<OperationCanceledException>(() => AnalysisBundleExporter.ExportStandard(path, data, new(), new(true)));
            Assert.Equal("previous export", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StandardExportHasStableFullDatasetResultsAndExplicitMissingModules()
    {
        using var data = Dataset();
        var first = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        var second = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try
        {
            AnalysisBundleExporter.ExportStandard(first, data, new());
            AnalysisBundleExporter.ExportStandard(second, data, new());
            using var a = ZipFile.OpenRead(first);
            using var b = ZipFile.OpenRead(second);
            foreach (var entry in new[] { "analyses.json", "signal_statistics.csv", "signal_minutes.csv", "signals.json", "source_registry.json" })
                Assert.Equal(Read(a, entry), Read(b, entry));
            using var manifest = JsonDocument.Parse(Read(a, "manifest.json"));
            Assert.Equal(3, manifest.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal("FullDataset", manifest.RootElement.GetProperty("AnalysisScope").GetString());
            Assert.Null(a.GetEntry("samples.csv"));
            using var analyses = JsonDocument.Parse(Read(a, "analyses.json"));
            Assert.Equal("FullDataset", analyses.RootElement.GetProperty("Scope").GetString());
            Assert.Contains("MissingSignals", Read(a, "analyses.json"));
            Assert.Contains("StableSignalId", Read(a, "signals.json"));
            Assert.Contains("unverified", Read(a, "manifest.json"));
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Fact]
    public void OptionalSamplesRetainOriginalSourceAndFrameIndices()
    {
        using var data = Dataset();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try
        {
            AnalysisBundleExporter.ExportStandard(path, data, new() { IncludeSamples = true });
            using var zip = ZipFile.OpenRead(path);
            var csv = Read(zip, "samples.csv");
            Assert.Contains("\"source_index\",\"source_frame_index\"", csv);
            Assert.Contains("\"0\",\"71\"", csv);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TemporaryStoresPreserveSourceIdentityThroughRandomAndSequentialReads()
    {
        using var frames = new DiskBackedFrameStore();
        var frame = new RawCanFrame(0L, 10, 1, [20], "DT", "1", false, 3, 9)
        { SourceFileIndex = 4, SourceFrameIndex = 17 };
        frames.Append(frame); frames.Complete();
        Assert.Equal(frame.SourceFrameIndex, frames[0].SourceFrameIndex);
        Assert.Equal(frame.Data, frames[0].Data);
        Assert.Equal(4, frames.Single().SourceFileIndex);
        using var samples = new DiskBackedDecodedSampleStore();
        var sample = new DecodedSignalSample(0, 3, 9, Identity, 20, 20, "C")
        { SourceFileIndex = 4, SourceFrameIndex = 17 };
        samples.Append(sample); samples.Complete();
        Assert.Equal(sample, samples[0]);
        Assert.Equal(17, samples.Single().SourceFrameIndex);
    }

    private static string Read(ZipArchive zip, string name)
    { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }

    [Fact]
    public void CompactSummaryKeepsNumericResultsAndMovesFullIntervalEvidenceToDeclaredDetailEntry()
    {
        using var data = Dataset();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try
        {
            var profile = new AnalysisProfile { DbcSha256 = data.DbcSha256, MaximumGapSeconds = 5,
                Usage = new() { Activity = Identity, Soc = Identity, ThresholdConfirmed = true, CapacityConfirmed = true,
                    Options = new() { ActivityThreshold = 1, MinimumActivitySeconds = 0, BridgePauseSeconds = 0 } } };
            var run = AnalysisBundleExporter.ExportStandard(path, data, new() { Profile = profile });
            using var zip = ZipFile.OpenRead(path);
            using var manifest = JsonDocument.Parse(Read(zip, "manifest.json"));
            Assert.Equal("analysis_details.json", manifest.RootElement.GetProperty("AnalysisDetailsEntry").GetString());
            using var summary = JsonDocument.Parse(Read(zip, "analyses.json"));
            using var full = JsonDocument.Parse(Read(zip, "analysis_details.json"));
            var usage = summary.RootElement.GetProperty("ActiveUsage").GetProperty("Value");
            Assert.False(usage.TryGetProperty("Intervals", out _));
            Assert.Equal(run.ActiveUsage.Value!.ActiveSeconds, usage.GetProperty("ActiveSeconds").GetDouble());
            Assert.Equal(run.ActiveUsage.Value.Intervals.Count,
                full.RootElement.GetProperty("ActiveUsage").GetProperty("Value").GetProperty("Intervals").GetArrayLength());
            Assert.DoesNotContain(".Intervals[", Read(zip, "analysis_values.csv"));
            var comparison = AnalysisBundleComparisonReader.Read(path);
            Assert.Equal(run.ActiveUsage.Value.ActivePercent, comparison.Metrics.Single(metric => metric.Key == "usage:active").Value);
            Assert.NotEmpty(comparison.Metrics.Single(metric => metric.Key == "usage:active").Compatibility);
        }
        finally { File.Delete(path); }
    }

    private static readonly SignalIdentity Identity = new("1", CanFrameFormat.Classic, false, 10, "Temperature", "Cell");
    private static CanDataset Dataset()
    {
        var series = new SignalSeries(Identity, [0L, 1_000_000_000L, 2_000_000_000L], [20d, 30d, 40d]);
        return new CanDataset
        {
            RawFrames = Enumerable.Range(0, 3).Select(i => new RawCanFrame(i * 1_000_000_000L, 10, 1, [(byte)(20 + i * 10)], "DT", "1", false, i)).ToArray(),
            DecodedSamples = Enumerable.Range(0, 3).Select(i => new DecodedSignalSample(i * 1_000_000_000L, i, i + 1, Identity, 20 + i * 10, 20 + i * 10, "C")
                { SourceFileIndex = 0, SourceFrameIndex = 71 + i }).ToArray(),
            SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries> { [Identity] = series },
            SignalSeriesByLabel = new Dictionary<string, SignalSeries> { [Identity.DisplayLabel] = series },
            SignalLabels = [Identity.DisplayLabel], MessageSummaries = [], Diagnostics = new(0, 0, 0, 0, 0, "test"),
            DbcSha256 = "test-dbc", SourceLogSha256 = "test-source"
        };
    }
}
