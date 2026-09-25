using System.Numerics;
using System.Reflection;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Parsing;
using CanAnalyzer.Core.Storage;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class PerformanceRegressionTests
{
    [Fact]
    public void CompactSampleStore_PreservesAllFieldsForSharedAndDifferentMetadata()
    {
        using var store = new DiskBackedDecodedSampleStore();
        var identity = new SignalIdentity("kanaal α", CanFrameFormat.FlexibleDataRate, true, 0x1fffffff, "Meting", "Positie");
        var samples = Enumerable.Range(0, 800).Select(i => new DecodedSignalSample(
            i * 12345678901L, i / 2, i + 12,
            i % 3 == 0 ? identity with { Channel = "ander", SignalName = "Positie β" } : identity,
            Math.Sin(i), i % 2 == 0 ? BigInteger.One << (i % 600) : -(BigInteger.One << (i % 600)),
            i % 5 == 0 ? "mm" : "µm", i % 2 == 0 ? DecodeQuality.Valid : DecodeQuality.PartialDataset)).ToArray();
        foreach (var sample in samples) store.Append(sample);
        store.Complete();
        Assert.Equal(samples, store.ToArray());
        foreach (var i in new[] { 799, 0, 511, 127, 257 }) Assert.Equal(samples[i], store[i]);
        Assert.Equal(samples[254..256], store.GetFrameSamples(127));
    }
    [Fact]
    public void DatasetDisposal_WaitsForReadersAndRejectsNewReaders()
    {
        using var frames = new DiskBackedFrameStore();
        frames.Append(new RawCanFrame(0, 0x123, 1, [42], "Rx", "1", false));
        using var dataset = new DatasetBuilder().Build(frames, [], [], new(0, 0, 0, 0, 0, ""));
        var first = dataset.AcquireReadLease(); var second = dataset.AcquireReadLease();
        dataset.Dispose();
        Assert.Throws<ObjectDisposedException>(() => dataset.AcquireReadLease());
        Assert.Equal(42, frames[0].Data[0]);
        first.Dispose(); first.Dispose();
        Assert.Equal(42, frames[0].Data[0]);
        second.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frames[0]);
        dataset.Dispose();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(255)]
    [InlineData(257)]
    [InlineData(1000)]
    public void SampledDiskReads_PreserveExactSelectionAcrossInterleavedBlocks(int budget)
    {
        using var store = new DiskBackedDecodedSampleStore();
        var first = new SignalIdentity("1", CanFrameFormat.FlexibleDataRate, true, 0x12345, "M", "A");
        var second = first with { SignalName = "B" };
        for (var i = 0; i < 1103; i++)
        {
            store.Append(new(i * 1234567L, i, i + 1, first, Math.Sin(i), i));
            store.Append(new(i * 1234567L, i, i + 1, second, -i, -i));
        }
        store.Complete();
        var actual = store.ReadSignalSeriesSampled(first, budget).ToArray();
        Assert.Equal(budget, actual.Length);
        for (var i = 0; i < budget; i++)
        {
            var source = (long)i * 1102 / (budget - 1);
            Assert.Equal(source, actual[i].FrameIndex);
            Assert.Equal(source * 1234567, actual[i].TimestampNanoseconds);
            Assert.Equal(Math.Sin(source), actual[i].Value);
        }
    }
    [Fact]
    public void LittleEndianExtraction_MatchesUnsignedReferenceForAllAlignmentsAndWidths()
    {
        var extract = typeof(CanDecodingService).GetMethod("TryExtractRaw", BindingFlags.NonPublic | BindingFlags.Static)!;
        var random = new Random(94312);
        foreach (var bytes in new[] { 8, 12, 64 })
        {
            var data = new byte[bytes]; random.NextBytes(data);
            var reference = new BigInteger(data, isUnsigned: true, isBigEndian: false);
            for (var offset = 0; offset < data.Length * 8; offset++)
            for (var width = 1; width <= Math.Min(80, data.Length * 8 - offset); width++)
            {
                var signal = new DbcSignal { Name = "Reference", StartBit = offset, Length = width,
                    IsLittleEndian = true, IsSigned = false, Scale = 1, Offset = 0,
                    Minimum = 0, Maximum = 0, Unit = "" };
                object[] args = [signal, data, BigInteger.Zero];
                Assert.True((bool)extract.Invoke(null, args)!);
                Assert.Equal((reference >> offset) & ((BigInteger.One << width) - 1), (BigInteger)args[2]);
            }
        }
    }

    [Fact]
    public void Downsampling_PreservesEndpointsPeaksAndOrderWithoutDuplicatingFlatSamples()
    {
        var x = Enumerable.Range(0, 10000).Select(i => (double)i).ToArray();
        var y = new double[x.Length]; y[1234] = -100; y[4321] = 100;
        var result = Downsampling.MinMax(x, y, 201);
        Assert.Equal(0, result.X[0]); Assert.Equal(9999, result.X[^1]);
        Assert.Contains(-100d, result.Y); Assert.Contains(100d, result.Y);
        Assert.InRange(result.X.Length, 2, 201);
        Assert.Equal(result.X.Length, result.X.Distinct().Count());
        Assert.True(result.X.SequenceEqual(result.X.Order()));
    }

    [Fact]
    public void RangeIndex_MatchesFullReductionAndRecoversExactZoomedData()
    {
        var x = Enumerable.Range(0, 10000).Select(i => i / 10d).ToArray();
        var y = x.Select(t => Math.Sin(t * 17)).ToArray(); y[4517] = 200;
        var index = new SignalRangeIndex(x, y);
        var expected = Downsampling.MinMax(x, y, 501);
        var actual = index.Select(null, null, 501);
        Assert.Equal(expected.X, actual.X); Assert.Equal(expected.Y, actual.Y);
        var zoom = index.Select(451, 452, 501);
        Assert.Equal(x[4509..4522], zoom.X); Assert.Contains(200d, zoom.Y);
        Assert.Empty(index.Select(2000, 2001, 501).X);
        Assert.Equal(expected.X, index.Select(null, null, 501).X);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PeakParser_ReleasesItsFilesWhenCancelledOrInputCannotOpen(bool cancelled)
    {
        var root = Path.Combine(Path.GetTempPath(), "canalyser-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.trc");
        var cache = Path.Combine(root, "cache");
        try
        {
            if (cancelled) await File.WriteAllTextAsync(input, ";$FILEVERSION=1.1\n");
            var parser = new PeakTrcParser(() => new DiskBackedFrameStore(cache));
            if (cancelled)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ParseAsync(input, ImportMode.Strict, null, new CancellationToken(true)));
            else
                await Assert.ThrowsAsync<FileNotFoundException>(() => parser.ParseAsync(input, ImportMode.Strict, null, CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(cache));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ConversionStorage_CountsNestedFilesWithoutCreatingMissingDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "canalyser-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, AnalysisCacheStorage.Inspect(root, true).Bytes);
            Assert.False(Directory.Exists(root));
            Directory.CreateDirectory(Path.Combine(root, "session", "output"));
            File.WriteAllBytes(Path.Combine(root, "session", "output", "part.trc"), new byte[123]);
            Assert.Equal(123, AnalysisCacheStorage.Inspect(root, true).Bytes);
            Assert.Equal(1, AnalysisCacheStorage.Inspect(root, true).Files);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
