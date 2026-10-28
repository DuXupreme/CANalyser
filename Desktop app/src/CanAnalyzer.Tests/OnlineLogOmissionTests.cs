using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Parsing;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class OnlineLogOmissionTests
{
    private static readonly byte[] Payload = "exact MF4 test input"u8.ToArray();
    private static string Name(int part, string session = "00000001") => $"48EDFD35/{session}/{part:00000000}.MF4";
    private static OnlineLogArchiveFile Included(int part, string session = "00000001") => new()
    {
        ArchiveName = Name(part, session), ObjectKey = "LOG/" + Name(part, session),
        LoggerId = "48EDFD35", Session = session, SizeBytes = Payload.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(Payload)), ObjectVersion = "version-" + part
    };
    private static OnlineLogArchiveOmission Omitted(int part, string session = "00000001") => new()
    {
        ArchiveName = Name(part, session), ObjectKey = "LOG/" + Name(part, session),
        LoggerId = "48EDFD35", Session = session, SizeBytes = Payload.Length,
        ObjectVersion = "version-" + part, Reason = "Werkbanktest, geen bruikbare machineopname", AnnotationRevision = "mark-2"
    };

    [Fact]
    public async Task ExplicitSmallOmissionBreaksCoverageWithoutChangingSamplesOrInventingTime()
    {
        var result = await Import(new() { SchemaVersion = 2, Files = [Included(1), Included(3)], OmittedFiles = [Omitted(2)] },
            [new(0, 100), new(200, 300)]);
        try
        {
            Assert.Equal(DatasetCompleteness.Partial, result.Completeness);
            Assert.Equal(new long[] { 0, 0, 100_000_000, 100_000_000, 200_000_000, 200_000_000, 300_000_000, 300_000_000 },
                result.Frames.Select(frame => frame.TimestampNanoseconds));
            var gap = Assert.Single(result.Report.Gaps);
            Assert.Equal(new MeasurementGap(.1, .2), gap);
            Assert.Equal([gap], result.Report.GetGapsForChannel("1"));
            Assert.Equal([gap], result.Report.GetGapsForChannel("2"));
            var boundary = Assert.Single(result.Report.ContinuityBreaks);
            Assert.Equal((0, 1, 100_000_000L, 200_000_000L), (boundary.BeforeSourceFileIndex,
                boundary.AfterSourceFileIndex, boundary.BeforeTimestampNanoseconds, boundary.AfterTimestampNanoseconds));
            Assert.Equal([Omitted(2).ObjectKey], boundary.OmittedObjectKeys);
            Assert.Equal(Omitted(2), Assert.Single(result.Report.Exclusions));
            Assert.Equal(2, result.SourceFiles.Count);
            Assert.All(result.SourceFiles, source => Assert.Equal(Payload.Length, source.SizeBytes));
            Assert.All(result.Frames.Take(4), frame => Assert.Equal(0, frame.SourceFileIndex));
            Assert.All(result.Frames.Skip(4), frame => Assert.Equal(1, frame.SourceFileIndex));

            var signal = new SignalSeries("activity", [0, .1, .2, .3], [1, 1, 1, 1]);
            var soc = new SignalSeries("soc", [0, .1, .2, .3], [90, 89, 40, 39]);
            var usage = new ActiveUsageAnalyzer().Analyze(signal, soc, null,
                new() { ActivityThreshold = 0, MinimumActivitySeconds = 0, BridgePauseSeconds = 10 },
                MeasurementGapSelection.ForChannel(result.Report, "1"));
            Assert.Equal(.2, usage.ActiveSeconds, 8);
            Assert.Equal(.1, usage.Duration(UsageState.Unknown), 8);
            Assert.Equal(2, usage.TotalEnergy.DischargeSocPoints, 8); // Never attribute the 49-point excluded jump.
        }
        finally { (result.Frames as IDisposable)?.Dispose(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnexplainedMiddleFileIsStillRejected(int schema)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(new()
        { SchemaVersion = schema, Files = [Included(1), Included(3)] }, [new(0, 100), new(200, 300)]));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("null-version")]
    [InlineData("reason")]
    [InlineData("revision")]
    [InlineData("identity")]
    [InlineData("duplicate")]
    [InlineData("v1")]
    public void OmissionNeedsVersionReasonRevisionAndUniqueMatchingIdentity(string invalid)
    {
        var omitted = Omitted(2);
        omitted = invalid switch
        {
            "version" => omitted with { ObjectVersion = "" }, "null-version" => omitted with { ObjectVersion = "null" },
            "reason" => omitted with { Reason = "" },
            "revision" => omitted with { AnnotationRevision = "" }, "identity" => omitted with { LoggerId = "OTHER" },
            "duplicate" => omitted with { ObjectKey = Included(1).ObjectKey }, _ => omitted
        };
        Assert.Throws<InvalidDataException>(() => OnlineLogArchiveManifestCodec.ValidateManifest(new()
        { SchemaVersion = invalid == "v1" ? 1 : 2, Files = [Included(1), Included(3)], OmittedFiles = [omitted] }));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(90)]
    public async Task EqualOrOverlappingBoundaryCannotGainArtificialDuration(double nextStartMilliseconds)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Import(new()
        { SchemaVersion = 2, Files = [Included(1), Included(3)], OmittedFiles = [Omitted(2)] },
            [new(0, 100), new(nextStartMilliseconds, 300)]));
        Assert.Contains("tijdgrens", error.Message);
    }

    [Fact]
    public async Task ExcludedPrefixAndSuffixHaveNoInferredDuration()
    {
        var result = await Import(new()
        { SchemaVersion = 2, Files = [Included(2)], OmittedFiles = [Omitted(1), Omitted(3)] }, [new(10, 100)]);
        try
        {
            Assert.Equal(2, result.Report.Exclusions.Count);
            Assert.Empty(result.Report.Gaps);
            Assert.Empty(result.Report.ContinuityBreaks);
            Assert.Equal(10_000_000, result.Frames[0].TimestampNanoseconds);
            Assert.Equal(100_000_000, result.Frames[^1].TimestampNanoseconds);
        }
        finally { (result.Frames as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task ExcludedSessionAlsoSeparatesTheIncludedSessions()
    {
        var result = await Import(new()
        { SchemaVersion = 2, Files = [Included(1, "00000001"), Included(1, "00000003")], OmittedFiles = [Omitted(1, "00000002")] },
            [new(0, 100), new(200, 300)]);
        try { Assert.Equal(new MeasurementGap(.1, .2), Assert.Single(result.Report.Gaps)); }
        finally { (result.Frames as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task OmittedBytesCannotSecretlyBeIncluded()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Import(new()
        { SchemaVersion = 2, Files = [Included(1), Included(3)], OmittedFiles = [Omitted(2)] },
            [new(0, 100), new(200, 300)], extraEntry: Name(2)));
        Assert.Contains("één-op-één", error.Message);
    }

    [Fact]
    public async Task IncludedBytesRemainHashBoundWithOmissions()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Import(new()
        { SchemaVersion = 2, Files = [Included(1) with { Sha256 = new string('0', 64) }, Included(3)], OmittedFiles = [Omitted(2)] },
            [new(0, 100), new(200, 300)]));
        Assert.Contains("SHA-256", error.Message);
    }

    private static async Task<CanLogParseResult> Import(OnlineLogArchiveManifest manifest, Trace[] traces, string? extraEntry = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"omissions-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var file in manifest.Files)
                {
                    await using var data = zip.CreateEntry(file.ArchiveName).Open(); await data.WriteAsync(Payload);
                }
                if (extraEntry is not null) { await using var extra = zip.CreateEntry(extraEntry).Open(); await extra.WriteAsync(Payload); }
                await using var metadata = zip.CreateEntry(OnlineLogArchiveManifestCodec.EntryName).Open();
                await OnlineLogArchiveManifestCodec.WriteAsync(metadata, manifest);
            }
            return (await new Mdf4Parser(new Converter(traces), new PeakTrcParser()).ParseAsync(path, ImportMode.Strict, null, default))!;
        }
        finally { File.Delete(path); }
    }

    private sealed record Trace(double StartMilliseconds, double EndMilliseconds);
    private sealed class Converter(Trace[] traces) : IMdf4ConversionService
    {
        public async Task<IReadOnlyList<string>> ConvertToPeakTrcAsync(IReadOnlyList<string> inputPaths,
            string outputDirectory, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(outputDirectory);
            var outputs = new List<string>();
            var origin = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc).ToOADate().ToString("R", CultureInfo.InvariantCulture);
            for (var i = 0; i < inputPaths.Count; i++)
            {
                var path = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPaths[i]) + "_CAN.trc");
                var rows = new[] { traces[i].StartMilliseconds, traces[i].EndMilliseconds }.SelectMany(time =>
                    new[] { "1", "2" }.Select(channel => $"1 {time.ToString("F3", CultureInfo.InvariantCulture)} DT {channel} 0200 Rx 1 AA"));
                await File.WriteAllTextAsync(path, $";$FILEVERSION=2.1\n;$STARTTIME={origin}\n;$COLUMNS=N,O,T,B,I,d,L,D\n" + string.Join('\n', rows), cancellationToken);
                outputs.Add(path);
            }
            outputs.Reverse(); // The parser must match source identity, not conversion result order.
            return outputs;
        }
    }
}
