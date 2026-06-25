using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Export;
using CanAnalyzer.Core.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class AnalyticsAndPipelineTests
{
    [Fact]
    public void AnalyticsSuite_ExercisesDelayTrackingRankingAndButterflyOnUnequalRates()
    {
        var service = new JoystickAnalyticsService();
        var fastTime = Enumerable.Range(0, 300).Select(index => index * 0.01d).ToArray();
        var slowTime = Enumerable.Range(0, 150).Select(index => index * 0.02d).ToArray();
        var command = fastTime.Select(time => Math.Sin(time * Math.PI * 1.2)).ToArray();
        var delayed = slowTime.Select(time => Math.Sin(Math.Max(0, time - 0.04d) * Math.PI * 1.2) * 0.9d + 0.05d).ToArray();
        var x = new SignalSeries("Joystick.X", fastTime, command);
        var y = new SignalSeries("Joystick.Y", slowTime, delayed);
        var responseX = new SignalSeries("Actuator.X", slowTime, delayed);
        var responseY = new SignalSeries("Actuator.Y", fastTime, command.Select(value => value * 0.95d).ToArray());

        var pair = service.AnalyzePair(x, y, 0.1d, 0.9d, 24, 1000);
        Assert.Equal(AnalysisStatus.Valid, pair.Alignment.Status);
        Assert.True(pair.Statistics.SampleCount > slowTime.Length);
        Assert.Equal(pair.Statistics.SampleCount, pair.RadiusHistogram.Sum(bin => bin.Count));

        var delay = service.AnalyzeDelay(x, responseX, 0.2d, 0.3d, 1000);
        Assert.True(delay.SampleCount > 100);
        Assert.InRange(Math.Abs(delay.BestLagSeconds), 0, 0.2d);
        Assert.NotEmpty(delay.CorrelationCurve);

        var tracking = service.AnalyzeJoystickActuatorTracking(x, y, responseX, responseY, 0.1d, 24, 1000);
        Assert.True(tracking.XAxis.SampleCount > 0);
        Assert.True(tracking.YAxis.SampleCount > 0);
        Assert.Equal(tracking.VectorErrorHistogram.Sum(bin => bin.Count), tracking.XAxis.SampleCount);

        var ranking = service.RankSignals(new Dictionary<string, SignalSeries> { [x.Label] = x, [y.Label] = y }, 0.001d, 2);
        Assert.Equal(2, ranking.Count);
        Assert.True(ranking[0].ActivityScore >= ranking[1].ActivityScore);

        var yaw = new SignalSeries("Joystick.Yaw", fastTime, command.Select(value => value * 0.2d).ToArray());
        var butterfly = service.AnalyzeButterflyKinematics(x, y, yaw, responseX, responseY, responseX, 0.1d, 0.2d, 16, 1000);
        Assert.True(butterfly.LeftTracking.SampleCount > 0);
        Assert.NotEmpty(butterfly.LeftCommandSeries);
        Assert.NotNull(butterfly.LeftDelay);
    }

    [Fact]
    public async Task Pipeline_EndToEndProducesHashesProvenanceLazySeriesAndCompleteStatus()
    {
        var logPath = Path.GetTempFileName();
        var dbcPath = Path.Combine(Path.GetTempPath(), $"pipeline_{Guid.NewGuid():N}.dbc");
        await File.WriteAllTextAsync(logPath, "(1.000000001) can1 123#2A\n(1.000000002) can1 123#2B\n");
        var message = new DbcMessage { RawFrameId = 0x123, IsExtendedFrame = false, Name = "M", Dlc = 1 };
        message.Signals.Add(Signal("S", 0, 8));
        await new DbcWriter().WriteAsync(new DbcDatabase { Messages = [message] }, dbcPath, CancellationToken.None);

        var parsing = new CanLogParsingService(
            new CssSemicolonParser(), new BusmasterParser(), new PeakTrcParser(), new CandumpParser(),
            new GenericTextCanParser(), NullLogger<CanLogParsingService>.Instance);
        var pipeline = new CanAnalysisPipeline(
            parsing, new DbcLoader(), new CanDecodingService(), new DatasetBuilder(),
            NullLogger<CanAnalysisPipeline>.Instance);
        try
        {
            using var dataset = await pipeline.LoadAsync(logPath, dbcPath, ImportMode.Strict, null, CancellationToken.None);
            Assert.Equal(DatasetCompleteness.Complete, dataset.Completeness);
            Assert.Equal(2, dataset.RawCount);
            Assert.Equal(2, dataset.DecodedSamples.Count);
            Assert.Equal(64, dataset.SourceLogSha256.Length);
            Assert.Equal(64, dataset.DbcSha256.Length);
            var series = Assert.Single(dataset.SignalSeriesByIdentity.Values);
            Assert.False(series.IsMaterialized);
            Assert.Equal([42d, 43d], series.Value);
            Assert.True(series.IsMaterialized);
        }
        finally
        {
            File.Delete(logPath);
            File.Delete(dbcPath);
        }
    }

    [Fact]
    public async Task DbcFloatAndExtendedMuxRangesSerializeAndLoadWithoutSilentLoss()
    {
        var mux = Signal("Mux", 0, 8, isMultiplexer: true);
        var ranged = Signal("Ranged", 8, 32, DbcSignalValueKind.IeeeFloat32,
            [new DbcMultiplexerRange("Mux", 2, 5)]);
        var message = new DbcMessage { RawFrameId = 0x123, IsExtendedFrame = false, Name = "M", Dlc = 8 };
        message.Signals.Add(mux);
        message.Signals.Add(ranged);
        var database = new DbcDatabase { Messages = [message] };
        var serialized = new DbcWriter().Serialize(database);
        Assert.Contains("SIG_VALTYPE_ 291 Ranged : 1;", serialized);
        Assert.Contains("SG_MUL_VAL_ 291 Ranged Mux 2-5;", serialized);

        var path = Path.Combine(Path.GetTempPath(), $"mux_{Guid.NewGuid():N}.dbc");
        try
        {
            await new DbcWriter().WriteAsync(database, path, CancellationToken.None);
            var loaded = await new DbcLoader().LoadAsync(path, CancellationToken.None);
            var loadedSignal = loaded.Messages.Single().Signals.Single(signal => signal.Name == "Ranged");
            Assert.Equal(DbcSignalValueKind.IeeeFloat32, loadedSignal.ValueKind);
            Assert.Contains(loadedSignal.MultiplexerRanges, range => range.Minimum == 2 && range.Maximum == 5);
            Assert.False(loaded.IsLosslessWritable);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new DbcWriter().WriteAsync(loaded, path + ".copy", CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".copy");
        }
    }

    [Fact]
    public void RawFiltersAndDownsamplingExerciseAllProfessionalViewBranches()
    {
        var frames = new[]
        {
            new RawCanFrame(0L, 0x100, 2, [0xAA, 0x01], "Rx", "1", false, 0, 1),
            new RawCanFrame(1_000_000_000L, 0x18FF50E5, 2, [0xBB, 0x02], "Tx", "2", true, 1, 2),
            new RawCanFrame(2_000_000_000L, 0x100, 2, [0xAA, 0x03], "Rx", "1", false, 2, 3)
        };
        var service = new RawFrameFilterService();
        Assert.Single(service.Apply(frames, new RawFrameFilterOptions
        {
            IdFilter = "0x100", DataContainsHex = "AA 03", TypeContains = "rx", ChannelContains = "1",
            TimeStart = 1.5d, TimeEnd = 2.5d, IsExtended = false, MaxRows = 10
        }));
        Assert.Single(service.Apply(frames, new RawFrameFilterOptions { IdFilter = "0x18FF50E5", IsExtended = true, MaxRows = 10 }));
        Assert.Empty(service.Apply(frames, new RawFrameFilterOptions { IdFilter = "not-an-id", MaxRows = 10 }));

        var x = Enumerable.Range(0, 1000).Select(index => (double)index).ToArray();
        var y = x.Select(Math.Sin).ToArray();
        var reduced = Downsampling.MinMax(x, y, 100);
        Assert.InRange(reduced.X.Length, 2, 108);
        var unchanged = Downsampling.MinMax(x, y, 2000);
        Assert.Same(x, unchanged.X);
        Assert.Same(y, unchanged.Y);
        var empty = Downsampling.MinMax([], [], 10);
        Assert.Empty(empty.X);
        Assert.Empty(empty.Y);
    }

    private static DbcSignal Signal(
        string name,
        int start,
        int length,
        DbcSignalValueKind valueKind = DbcSignalValueKind.Integer,
        IReadOnlyList<DbcMultiplexerRange>? ranges = null,
        bool isMultiplexer = false) => new()
    {
        Name = name,
        StartBit = start,
        Length = length,
        IsLittleEndian = true,
        IsSigned = false,
        Scale = 1,
        Offset = 0,
        Minimum = 0,
        Maximum = 100,
        Unit = string.Empty,
        ValueKind = valueKind,
        MultiplexerRanges = ranges ?? [],
        IsMultiplexer = isMultiplexer
    };
}
