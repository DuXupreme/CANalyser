using System.Collections;
using System.Diagnostics;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CanAnalyzer.Tests;

public sealed class LoadPerformanceBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task CachedOnlineImport()
    {
        var logPath = Environment.GetEnvironmentVariable("CANALYSER_BENCHMARK_LOG");
        var dbcPath = Environment.GetEnvironmentVariable("CANALYSER_BENCHMARK_DBC");
        if (string.IsNullOrWhiteSpace(logPath) || string.IsNullOrWhiteSpace(dbcPath)) return;
        var peak = new PeakTrcParser();
        var parsing = new CanLogParsingService(new CssSemicolonParser(), new BusmasterParser(), peak,
            new CandumpParser(), new GenericTextCanParser(), NullLogger<CanLogParsingService>.Instance,
            new Mdf4Parser(new Mdf4ConversionService(NullLogger<Mdf4ConversionService>.Instance), peak));
        var pipeline = new CanAnalysisPipeline(parsing, new DbcLoader(), new CanDecodingService(), new DatasetBuilder(),
            NullLogger<CanAnalysisPipeline>.Instance);
        var timer = Stopwatch.StartNew();
        using var prepared = await pipeline.PrepareForReviewAsync(logPath, dbcPath, null, CancellationToken.None);
        using var dataset = prepared.Accept(allowPartial: true);
        Assert.True(dataset.ImportReport!.IsConsistent);
        Assert.True(dataset.RawCount > 0);
        Assert.True(dataset.SignalCount > 0);
        output.WriteLine($"total_ms={timer.ElapsedMilliseconds}; frames={dataset.RawCount}; samples={dataset.DecodedSamples.Count}; signals={dataset.SignalCount}; completeness={dataset.Completeness}; decode_errors={dataset.Diagnostics.DecodeErrorFrameCount}");
        output.WriteLine($"phases={dataset.LoadTimings}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void DecodeBuildAndReadEverySignal()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("CANALYSER_LOAD_BENCHMARK_FRAMES"), out var count) || count <= 0)
            return;

        var message = new DbcMessage { RawFrameId = 0x123, Name = "Benchmark", Dlc = 8, IsExtendedFrame = false };
        for (var index = 0; index < 8; index++)
            message.Signals.Add(new DbcSignal
            {
                Name = $"S{index}", StartBit = index * 8, Length = 8, IsLittleEndian = true,
                IsSigned = false, Scale = 1, Offset = 0, Minimum = 0, Maximum = 255, Unit = "unit"
            });
        var frames = new GeneratedFrames(count);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        var decoded = new CanDecodingService().Decode(frames, new DbcDatabase { Messages = [message] }, null, CancellationToken.None);
        var decodeMs = timer.ElapsedMilliseconds;
        timer.Restart();
        using var dataset = new DatasetBuilder().Build(frames, decoded.Samples, decoded.MessageSummaries, decoded.Diagnostics);
        var buildMs = timer.ElapsedMilliseconds;
        timer.Restart();
        var points = dataset.SignalSeriesByIdentity.Values.Sum(series => (long)series.Value.Length);
        var seriesMs = timer.ElapsedMilliseconds;
        Assert.Equal((count - (count + 4) / 5) * 8L, points);
        Assert.Equal(points, dataset.DecodedSamples.Count);
        output.WriteLine($"frames={count}; samples={points}; decode_ms={decodeMs}; build_ms={buildMs}; all_series_ms={seriesMs}; allocated_mb={(GC.GetTotalAllocatedBytes(true) - allocated) / 1_000_000}");
    }

    private sealed class GeneratedFrames(int count) : IReadOnlyList<RawCanFrame>
    {
        public int Count => count;
        public RawCanFrame this[int index] => new(index * 1_000L, index % 5 == 0 ? 0x456u : 0x123u, 8,
            [0, 1, 2, 3, 4, 5, 6, 7], "Rx", (index % 2).ToString(), false, index, index + 1L);
        public IEnumerator<RawCanFrame> GetEnumerator()
        {
            for (var index = 0; index < count; index++) yield return this[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
