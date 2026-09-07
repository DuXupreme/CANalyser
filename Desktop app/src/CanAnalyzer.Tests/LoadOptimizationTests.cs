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
    [InlineData("22484AAA/00000025/00000010.MF4", "22484AAA", "00000025", "00000010.MF4")]
    [InlineData("prefix/48EDFD35/00000008/00000001.MF4", "48EDFD35", "00000008", "00000001.MF4")]
    [InlineData("log.MF4", null, null, "log.MF4")]
    [InlineData("", null, null, "")]
    public void ArchiveSource_PreservesOriginalNamesAndSession(string path, string? logger, string? session, string name)
    {
        var source = SourceLogFile.FromArchiveEntry(path);
        Assert.Equal(name, source.Name);
        Assert.Equal(logger, source.Logger);
        Assert.Equal(session, source.Session);
        Assert.Equal(path, source.SourcePath);
    }

    [Theory]
    [InlineData("C:/logs/22484AAA/00000025/log.MF4", "22484AAA", "00000025")]
    [InlineData("C:/logs/22484aaa/00000025/log.mf4", "22484aaa", "00000025")]
    [InlineData("C:/customer/project/log.MF4", null, null)]
    [InlineData("C:/logs/ZZZZZZZZ/session/log.MF4", null, null)]
    [InlineData("C:/logs/22484AAA/session/log.trc", null, null)]
    [InlineData("log.mf4", null, null)]
    public void LocalSource_DoesNotInventIdentityFromArbitraryFolders(string path, string? logger, string? session)
    {
        var source = SourceLogFile.FromLocalFile(path.Replace('/', '\\'));
        Assert.Equal(logger, source.Logger);
        Assert.Equal(session, source.Session);
        Assert.Equal(path, source.SourcePath);
    }

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

    [Fact]
    public void DiskBackedSeries_BoundedAnalysisCopyUsesIndexWithoutMaterializingSource()
    {
        using var store = new DiskBackedDecodedSampleStore();
        var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 0x321, "Message", "Signal");
        const int sourceCount = 100_003;
        for (var index = 0; index < sourceCount; index++)
        {
            store.Append(new DecodedSignalSample(index * 1000L, index, index + 1L, identity, index, index));
        }

        store.Complete();
        using var dataset = new DatasetBuilder().Build([], store, [], new DecoderDiagnostics(0, 0, 1, 0, 0, string.Empty));
        var source = dataset.SignalSeriesByIdentity[identity];
        var bounded = source.ForAnalysis(1000);

        Assert.False(source.IsMaterialized);
        Assert.Equal(sourceCount, source.SampleCount);
        Assert.Equal(1000, bounded.Value.Length);
        Assert.Equal(0, bounded.Value[0]);
        Assert.Equal(sourceCount - 1, bounded.Value[^1]);
        Assert.True(bounded.Time.Zip(bounded.Time.Skip(1), (left, right) => right > left).All(value => value));
    }

    [Fact]
    public void FifteenMillionPointLazySeries_UsesBoundedLoaderForAnalysis()
    {
        var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 0x123, "Message", "Signal");
        var fullLoaderCalled = false;
        var requestedMaximum = 0;
        var source = new SignalSeries(
            identity,
            15_000_000,
            () =>
            {
                fullLoaderCalled = true;
                return ([], []);
            },
            maximum =>
            {
                requestedMaximum = maximum;
                return (
                    Enumerable.Range(0, maximum).Select(index => (long)index).ToArray(),
                    Enumerable.Range(0, maximum).Select(index => (double)index).ToArray());
            });

        var bounded = source.ForAnalysis(250_000);

        Assert.False(fullLoaderCalled);
        Assert.False(source.IsMaterialized);
        Assert.Equal(250_000, requestedMaximum);
        Assert.Equal(250_000, bounded.SampleCount);
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
            Assert.Equal(Path.GetFullPath(logPath), dataset.SourceLogPath);
            Assert.Equal(Path.GetFullPath(dbcPath), dataset.SourceDbcPath);
            Assert.Equal(Path.GetFileName(logPath), Assert.Single(dataset.SourceFiles).Name);
            Assert.Null(dataset.SourceFiles[0].Logger);
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
