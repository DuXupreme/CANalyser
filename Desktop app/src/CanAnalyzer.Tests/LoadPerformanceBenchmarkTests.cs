using System.Collections;
using System.Diagnostics;
using System.Text.Json;
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
        using var trace = BenchmarkTrace.Create(Environment.GetEnvironmentVariable("CANALYSER_BENCHMARK_TRACE"));
        var timer = Stopwatch.StartNew();
        using var prepared = await pipeline.PrepareForReviewAsync(logPath, dbcPath, trace, CancellationToken.None);
        using var dataset = prepared.Accept(allowPartial: true);
        Assert.True(dataset.ImportReport!.IsConsistent);
        Assert.True(dataset.RawCount > 0);
        Assert.True(dataset.SignalCount > 0);
        output.WriteLine($"total_ms={timer.ElapsedMilliseconds}; frames={dataset.RawCount}; samples={dataset.DecodedSamples.Count}; signals={dataset.SignalCount}; completeness={dataset.Completeness}; decode_errors={dataset.Diagnostics.DecodeErrorFrameCount}");
        output.WriteLine($"phases={dataset.LoadTimings}");
        timer.Restart();
        trace?.Report(new("Grafiekbron en viewport voorbereiden", 100));
        var signal = dataset.SignalSeriesByIdentity.Values.OrderByDescending(series => series.SampleCount).First();
        var index = new SignalRangeIndex(signal.Time, signal.Value);
        var graphPreparationMs = timer.Elapsed.TotalMilliseconds;
        var navigation = new double[100];
        var first = signal.Time[0]; var span = Math.Max(.001, signal.Time[^1] - first);
        for (var i = 0; i < navigation.Length; i++)
        {
            timer.Restart();
            var window = index.Select(first + span * i / 110, first + span * i / 110 + span / 20, 2000);
            navigation[i] = timer.Elapsed.TotalMilliseconds;
            Assert.InRange(window.X.Length, 0, 2000);
        }
        Array.Sort(navigation);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        output.WriteLine($"graph_prepare_ms={graphPreparationMs:F3}; viewport_median_ms={navigation[50]:F3}; viewport_p95_ms={navigation[94]:F3}; peak_working_set_mb={process.PeakWorkingSet64 / 1_000_000d:F1}; managed_heap_mb={GC.GetTotalMemory(false) / 1_000_000d:F1}");
        var exportPath = Environment.GetEnvironmentVariable("CANALYSER_BENCHMARK_EXPORT");
        if (!string.IsNullOrWhiteSpace(exportPath))
        {
            trace?.Report(new("Standaardanalyse en bundel exporteren", 100));
            timer.Restart();
            var run = AnalysisBundleExporter.ExportStandard(exportPath, dataset, new());
            var exportMs = timer.ElapsedMilliseconds;
            process.Refresh();
            trace?.Report(new("Geëxporteerde bundel teruglezen", 100));
            var reopened = AnalysisBundleComparisonReader.Read(exportPath);
            Assert.Equal(3, reopened.SchemaVersion);
            Assert.True(reopened.FullDataset);
            Assert.True(reopened.SourceIdentityComplete);
            output.WriteLine($"standard_export_ms={exportMs}; export_bytes={new FileInfo(exportPath).Length}; export_peak_working_set_mb={process.PeakWorkingSet64 / 1_000_000d:F1}; materialized_signals={dataset.SignalSeriesByIdentity.Values.Count(series => series.IsMaterialized)}/{dataset.SignalCount}; joystick_modules={run.Joysticks.Count}; actuator_modules={run.Actuators.Count}; can_channels={run.CanChannels.Count}");
        }
    }

    /// <summary>Opt-in diagnostic sampling; CPU is this managed test process, excluding external converter children.</summary>
    private sealed class BenchmarkTrace : IProgress<LoadProgress>, IDisposable
    {
        private readonly object _gate = new();
        private readonly string _path;
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly Timer _timer;
        private readonly TimeSpan _initialCpu;
        private string _phase = "Start";
        private int _percent;
        private bool _disposed;

        private BenchmarkTrace(string path)
        {
            _path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _initialCpu = _process.TotalProcessorTime;
            _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        public static BenchmarkTrace? Create(string? path) => string.IsNullOrWhiteSpace(path) ? null : new(path);
        public void Report(LoadProgress value) { lock (_gate) { _phase = value.Label; _percent = value.Percent; } }
        private void Sample()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _process.Refresh();
                File.AppendAllText(_path, JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, wall_ms = _wall.ElapsedMilliseconds,
                    cpu_ms = (_process.TotalProcessorTime - _initialCpu).TotalMilliseconds,
                    cpu_scope = "test_process_excludes_converter_children", phase = _phase, percent = _percent,
                    working_set_mb = _process.WorkingSet64 / 1_000_000d, managed_heap_mb = GC.GetTotalMemory(false) / 1_000_000d,
                    gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2)
                }) + Environment.NewLine);
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            lock (_gate)
            {
                Sample();
                _disposed = true;
                _process.Dispose();
            }
        }
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
