using System.Numerics;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Parsing;
using CanAnalyzer.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class LoadOptimizationTests
{
    [Theory]
    [InlineData(DatasetCompleteness.Complete)]
    [InlineData(DatasetCompleteness.Partial)]
    public void SignalIndex_PreservesAllPointsStableOrderStatisticsAndExactRawValues(DatasetCompleteness completeness)
    {
        using var store = new DiskBackedDecodedSampleStore();
        var identities = new[]
        {
            new SignalIdentity("1", CanFrameFormat.Classic, false, 0x123, "Message", "Signal"),
            new SignalIdentity("2", CanFrameFormat.Classic, false, 0x123, "Message", "Signal"),
            new SignalIdentity("1", CanFrameFormat.FlexibleDataRate, true, 0x123, "Message", "Signal")
        };
        var samples = new List<DecodedSignalSample>();
        for (var frame = 0; frame < 777; frame++)
        {
            foreach (var identity in identities)
            {
                // Multiple full blocks, a partial final block, out-of-order and duplicate timestamps.
                var sample = new DecodedSignalSample((frame % 19) * 1_000_000_001L, frame, frame + 1,
                    identity, frame - 300, (BigInteger.One << 100) + frame, frame % 2 == 0 ? "mV" : "V");
                samples.Add(sample);
                store.Append(sample);
            }
        }
        store.Complete();
        var diagnostics = new DecoderDiagnostics(0, 0, 1, 0, 0, string.Empty);
        using var indexed = new DatasetBuilder().Build([], store, [], diagnostics, completeness: completeness);
        using var reference = new DatasetBuilder().Build([], samples, [], diagnostics, completeness: completeness);
        Assert.All(indexed.SignalSeriesByIdentity.Values, series => Assert.False(series.IsMaterialized));
        var lookup = Assert.IsAssignableFrom<ISignalSampleLookup>(indexed.DecodedSamples);
        foreach (var identity in identities)
        {
            var expected = reference.SignalSeriesByIdentity[identity];
            var actual = indexed.SignalSeriesByIdentity[identity];
            Assert.Equal(expected.TimestampNanoseconds, actual.TimestampNanoseconds);
            Assert.Equal(expected.Value, actual.Value);
            var summary = Assert.Single(lookup.GetSignalSummaries(), summary => summary.Latest.Identity == identity);
            Assert.Equal(777, summary.Count);
            Assert.Equal(-300, summary.Minimum);
            Assert.Equal(476, summary.Maximum);
            var latest = samples.Where(sample => sample.Identity == identity)
                .OrderBy(sample => sample.TimestampNanoseconds).ThenBy(sample => sample.FrameIndex).Last();
            Assert.Equal(latest with { Quality = completeness == DatasetCompleteness.Partial ? DecodeQuality.PartialDataset : DecodeQuality.Valid }, summary.Latest);
        }
        Assert.Empty(lookup.ReadSignalSeries(identities[0] with { SignalName = "absent" }));
        Assert.Equal(samples.Select(sample => sample.RawValue), indexed.DecodedSamples.Select(sample => sample.RawValue));
        Assert.Equal(samples[1024].RawValue, store[1024].RawValue);
        Assert.Equal(3, store.GetFrameSamples(400).Count);
    }

    [Fact]
    public void EmptyAndDisposedSignalIndex_RespectStoreLifetime()
    {
        var store = new DiskBackedDecodedSampleStore();
        Assert.Empty(store.GetSignalSummaries());
        store.Dispose();
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.GetSignalSummaries());
        var unfinished = new DiskBackedDecodedSampleStore();
        unfinished.Append(new DecodedSignalSample(1, 0, 1,
            new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "M", "S"), 42, 42));
        unfinished.Dispose();
        Assert.Throws<ObjectDisposedException>(() => unfinished.GetSignalSummaries());
    }

    [Theory]
    [InlineData("(1.000000001) can1 123#2A00\n", false)]
    [InlineData("(1.000000001) can1 123#2A00\n(1.1) can1 123#2B\n", true)]
    [InlineData("(1.000000001) can1 123#2A00\ninvalid data line\n", true)]
    public async Task Review_RequiresExplicitConsentAndAcceptsWithoutReprocessing(string log, bool hasErrors)
    {
        var logPath = Path.GetTempFileName();
        var dbcPath = Path.GetTempFileName();
        var parsing = new CountingParsingService();
        var decoder = new CountingDecoder();
        var pipeline = new CanAnalysisPipeline(parsing, new DbcLoader(), decoder, new DatasetBuilder(), NullLogger<CanAnalysisPipeline>.Instance);
        try
        {
            await File.WriteAllTextAsync(logPath, log);
            await File.WriteAllTextAsync(dbcPath, "BO_ 291 M: 2 Vector__XXX\n SG_ S : 0|8@1+ (1,0) [0|255] \"V\" Vector__XXX\n");
            using var prepared = await pipeline.PrepareForReviewAsync(logPath, dbcPath, null, CancellationToken.None);
            Assert.Equal(hasErrors, prepared.RequiresPartialConfirmation);
            if (hasErrors) Assert.Throws<InvalidOperationException>(() => prepared.Accept());
            Assert.True(await prepared.MatchesSourcesAsync(logPath, dbcPath, CancellationToken.None));
            using var dataset = prepared.Accept(allowPartial: hasErrors);
            prepared.Dispose(); // Ownership was transferred; the accepted dataset remains readable.
            Assert.Equal(42, Assert.Single(dataset.DecodedSamples).Value);
            Assert.Equal(hasErrors ? DecodeQuality.PartialDataset : DecodeQuality.Valid, dataset.DecodedSamples[0].Quality);
            Assert.Equal(hasErrors ? ImportMode.Partial : ImportMode.Strict, dataset.ImportReport!.Mode);
            Assert.Equal(1, parsing.Calls);
            Assert.Equal(1, decoder.Calls);
            Assert.NotNull(dataset.LoadTimings);
            Assert.Throws<ObjectDisposedException>(() => prepared.Accept(allowPartial: true));

            if (hasErrors)
                await Assert.ThrowsAsync<ImportIntegrityException>(() => pipeline.LoadAsync(logPath, dbcPath, ImportMode.Strict, null, CancellationToken.None));
        }
        finally
        {
            File.Delete(logPath);
            File.Delete(dbcPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_RejectsChangedSourceContentsAndDisposesUnacceptedData(bool changeDbc)
    {
        var logPath = Path.GetTempFileName();
        var dbcPath = Path.GetTempFileName();
        var decoder = new CountingDecoder();
        var parsing = new CountingParsingService();
        var pipeline = new CanAnalysisPipeline(parsing, new DbcLoader(), decoder, new DatasetBuilder(), NullLogger<CanAnalysisPipeline>.Instance);
        try
        {
            await File.WriteAllTextAsync(logPath, "(1.0) can1 123#2A00\n");
            await File.WriteAllTextAsync(dbcPath, "BO_ 291 M: 2 Vector__XXX\n SG_ S : 0|8@1+ (1,0) [0|255] \"V\" Vector__XXX\n");
            var prepared = await pipeline.PrepareForReviewAsync(logPath, dbcPath, null, CancellationToken.None);
            using (prepared)
            {
                // Same path and same length must not cause stale results to be reused.
                var path = changeDbc ? dbcPath : logPath;
                var text = await File.ReadAllTextAsync(path);
                await File.WriteAllTextAsync(path, changeDbc ? text.Replace("(1,0)", "(2,0)") : text.Replace("2A00", "2B00"));
                Assert.False(await prepared.MatchesSourcesAsync(logPath, dbcPath, CancellationToken.None));
            }
            Assert.Throws<ObjectDisposedException>(() => decoder.LastSamples![0]);
            Assert.Throws<ObjectDisposedException>(() => parsing.LastFrames![0]);
        }
        finally
        {
            File.Delete(logPath);
            File.Delete(dbcPath);
        }
    }

    [Fact]
    public void FrameMetadata_CountsExtendedFramesAndDistinctChannelsDuringAppend()
    {
        using var frames = new DiskBackedFrameStore();
        frames.Append(new RawCanFrame(0L, 0x123, 1, [1], "Rx", "1", false, 0, 1));
        frames.Append(new RawCanFrame(1L, 0x123, 1, [2], "Rx", "2", true, 1, 2));
        frames.Append(new RawCanFrame(2L, 0x123, 1, [3], "Rx", "1", false, 2, 3));
        frames.Complete();
        Assert.Equal(1, frames.ExtendedCount);
        Assert.Equal(["1", "2"], frames.Channels.Order());
    }

    [Theory]
    [InlineData("unchanged", 1, 1)]
    [InlineData("dbc", 1, 2)]
    [InlineData("log", 2, 2)]
    public async Task Retry_ReusesOnlyUnchangedSources(string change, int parses, int decodes)
    {
        var logPath = Path.GetTempFileName();
        var dbcPath = Path.GetTempFileName();
        var parsing = new CountingParsingService();
        var decoder = new CountingDecoder();
        var pipeline = new CanAnalysisPipeline(parsing, new DbcLoader(), decoder, new DatasetBuilder(), NullLogger<CanAnalysisPipeline>.Instance);
        try
        {
            await File.WriteAllTextAsync(logPath, "(1.0) can1 123#2A\n");
            var dbc = "BO_ 291 M: 2 Vector__XXX\n SG_ S : 0|8@1+ (1,0) [0|255] \"V\" Vector__XXX\n";
            await File.WriteAllTextAsync(dbcPath, dbc);
            using var original = await pipeline.PrepareForReviewAsync(logPath, dbcPath, null, CancellationToken.None);
            Assert.True(original.RequiresPartialConfirmation);
            if (change == "dbc") await File.WriteAllTextAsync(dbcPath, dbc.Replace("M: 2", "M: 1"));
            if (change == "log") await File.WriteAllTextAsync(logPath, "(1.0) can1 123#2B00\n");
            using var retry = await pipeline.RetryForReviewAsync(original, logPath, dbcPath, null, CancellationToken.None);
            Assert.Equal(parses, parsing.Calls);
            Assert.Equal(decodes, decoder.Calls);
            Assert.Equal(change == "unchanged", retry.RequiresPartialConfirmation);
            if (change == "unchanged") Assert.Same(original, retry);
            using var dataset = retry.Accept(allowPartial: change == "unchanged");
            original.Dispose();
            Assert.Single(dataset.RawFrames);
            if (change != "unchanged")
            {
                Assert.Equal(change == "dbc" ? 42 : 43, Assert.Single(dataset.DecodedSamples).Value);
                Assert.False(dataset.ImportReport!.HasErrors);
                Assert.Equal(ImportMode.Strict, dataset.ImportReport.Mode);
            }
        }
        finally { File.Delete(logPath); File.Delete(dbcPath); }
    }

    private sealed class CountingParsingService : ICanLogParsingService
    {
        public int Calls;
        public IReadOnlyList<RawCanFrame>? LastFrames;
        public async Task<CanLogParseResult> ParseAsync(string path, ImportMode mode, IProgress<LoadProgress>? progress, CancellationToken token)
        {
            Calls++;
            var service = new CanLogParsingService(new CssSemicolonParser(), new BusmasterParser(), new PeakTrcParser(),
                new CandumpParser(), new GenericTextCanParser(), NullLogger<CanLogParsingService>.Instance);
            var result = await service.ParseAsync(path, mode, progress, token);
            LastFrames = result.Frames;
            return result;
        }
    }

    private sealed class CountingDecoder : ICanDecodingService
    {
        public int Calls;
        public IReadOnlyList<DecodedSignalSample>? LastSamples;
        public DecodeResult Decode(IReadOnlyList<RawCanFrame> frames, DbcDatabase database, IProgress<LoadProgress>? progress, CancellationToken token)
        {
            Calls++;
            var result = new CanDecodingService().Decode(frames, database, progress, token);
            LastSamples = result.Samples;
            return result;
        }
    }
}
