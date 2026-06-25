using System.Globalization;
using System.Numerics;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Export;
using CanAnalyzer.Core.Parsing;
using CanAnalyzer.Core.Storage;
using CanAnalyzer.Core.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class DataIntegrityTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1.000000001", 1_000_000_001L)]
    [InlineData("864000.123456789", 864_000_123_456_789L)]
    public void DecimalTimestamp_IsExactToNanosecond(string text, long expected) =>
        Assert.Equal(expected, TimestampParsers.ParseDecimalSecondsToNanoseconds(text));

    [Fact]
    public async Task CandumpFd_PreservesFlagsChannelDlcAndLineAccounting()
    {
        var path = await WriteTempAsync("# header\n(100.000000001) can2 123##1AABBCCDDEEFF001122334455\ncorrupt data line\n");
        try
        {
            var result = await new CandumpParser().ParseAsync(path, ImportMode.Partial, null, CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(3, result!.Report.TotalLines);
            Assert.Equal(1, result.Report.NonDataLines);
            Assert.Equal(1, result.Report.AcceptedLines);
            Assert.Equal(1, result.Report.RejectedLines);
            Assert.True(result.Report.IsConsistent);
            var frame = Assert.Single(result.Frames);
            Assert.Equal("can2", frame.Channel);
            Assert.Equal(CanFrameFormat.FlexibleDataRate, frame.FrameFormat);
            Assert.True(frame.BitRateSwitch);
            Assert.Equal(12, frame.PayloadLength);
            Assert.Equal(9, frame.Dlc);
            Assert.Equal(2, frame.SourceLineNumber);
            (result.Frames as IDisposable)?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Busmaster_MidnightRolloverRemainsMonotonicAndExact()
    {
        var path = await WriteTempAsync(
            "23:59:59:999999999 Rx 1 123 d 1 AA\n" +
            "00:00:00:000000001 Rx 1 123 d 1 BB\n");
        try
        {
            var result = await new BusmasterParser().ParseAsync(path, ImportMode.Partial, null, CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(0, result!.Frames[0].TimestampNanoseconds);
            Assert.Equal(2, result.Frames[1].TimestampNanoseconds);
            Assert.True(result.Report.IsConsistent);
            (result.Frames as IDisposable)?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PeakParser_PreservesMillisecondsDirectionAndProvenance()
    {
        var path = await WriteTempAsync(";$FILEVERSION=2.1\n1) 1234.000001 Tx 123 2 AA BB\n");
        try
        {
            var result = await new PeakTrcParser().ParseAsync(path, ImportMode.Partial, null, CancellationToken.None);
            Assert.NotNull(result);
            var frame = Assert.Single(result!.Frames);
            Assert.Equal(1_234_000_001L, frame.TimestampNanoseconds);
            Assert.Equal(CanFrameDirection.Transmit, frame.Direction);
            Assert.Equal(2, frame.SourceLineNumber);
            Assert.True(result.Report.IsConsistent);
            (result.Frames as IDisposable)?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task GenericParser_IsExplicitAndStillAccountsForEveryLine()
    {
        var parser = new GenericTextCanParser();
        Assert.Equal(0, parser.Probe("anything.txt", ["1.0 123 1 AA"]));
        var path = await WriteTempAsync("1.000000001 123 1 AA\ninvalid\n");
        try
        {
            var result = await parser.ParseAsync(path, ImportMode.Partial, null, CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(1_000_000_001L, result!.Frames[0].TimestampNanoseconds);
            Assert.Equal(2, result.Report.TotalLines);
            Assert.Equal(1, result.Report.AcceptedLines);
            Assert.Equal(1, result.Report.RejectedLines);
            Assert.True(result.Report.IsConsistent);
            (result.Frames as IDisposable)?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ParsingService_StrictBlocksRejectedDataAndPartialRequiresExplicitMode()
    {
        var path = await WriteTempAsync("Timestamp;Type;ID;Data\n01T000000001;0;123;AA\ncorrupt\n");
        var service = new CanLogParsingService(
            new CssSemicolonParser(), new BusmasterParser(), new PeakTrcParser(), new CandumpParser(),
            new GenericTextCanParser(), NullLogger<CanLogParsingService>.Instance);
        try
        {
            var exception = await Assert.ThrowsAsync<ImportIntegrityException>(() =>
                service.ParseAsync(path, ImportMode.Strict, null, CancellationToken.None));
            Assert.Equal(1, exception.Report.RejectedLines);
            Assert.True(exception.Report.IsConsistent);

            var partial = await service.ParseAsync(path, ImportMode.Partial, null, CancellationToken.None);
            Assert.Equal(DatasetCompleteness.Partial, partial.Completeness);
            Assert.Equal(ImportMode.Partial, partial.Report.Mode);
            (partial.Frames as IDisposable)?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Decoder_PreservesUnsigned64BitRawExactly()
    {
        var message = Message(0x321, 8, Signal("Raw64", 0, 64));
        var bytes = new byte[] { 0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0xF1 };
        var frame = Frame(0, 0x321, bytes);
        var result = new CanDecodingService().Decode([frame], new DbcDatabase { Messages = [message] }, null, CancellationToken.None);
        var sample = Assert.Single(result.Samples);
        Assert.Equal(BigInteger.Parse("17375808098319191535", CultureInfo.InvariantCulture), sample.RawValue);
        Assert.Equal(frame.FrameIndex, sample.FrameIndex);
        (result.Samples as IDisposable)?.Dispose();
    }

    [Fact]
    public void Decoder_HandlesSignedMotorolaAndIeee64WithoutRawLoss()
    {
        var motorola = Signal("Motorola", 7, 16, littleEndian: false);
        var signed = Signal("Signed", 16, 8, signed: true);
        var ieee = Signal("Float64", 24, 64, valueKind: DbcSignalValueKind.IeeeFloat64);
        var message = Message(0x333, 12, motorola, signed, ieee);
        var data = new byte[12];
        data[0] = 0x12;
        data[1] = 0x34;
        data[2] = 0xFF;
        BitConverter.GetBytes(1.25d).CopyTo(data, 3);
        var frame = new RawCanFrame(0L, 0x333, 9, data, "Rx", "1", false, 0, 1, CanFrameFormat.FlexibleDataRate);
        var result = new CanDecodingService().Decode([frame], new DbcDatabase { Messages = [message] }, null, CancellationToken.None);
        Assert.Equal(new BigInteger(0x1234), result.Samples.Single(sample => sample.SignalName == "Motorola").RawValue);
        Assert.Equal(-1d, result.Samples.Single(sample => sample.SignalName == "Signed").Value);
        Assert.Equal(new BigInteger(0xFF), result.Samples.Single(sample => sample.SignalName == "Signed").RawValue);
        Assert.Equal(1.25d, result.Samples.Single(sample => sample.SignalName == "Float64").Value);
        (result.Samples as IDisposable)?.Dispose();
    }

    [Fact]
    public void Decoder_MultiplexesOnExactRawCodeNotScaledPhysicalValue()
    {
        var mux = Signal("Mux", 0, 8, scale: 0.4, isMultiplexer: true);
        var selected = Signal("Selected", 8, 8, multiplexerIds: [1]);
        var message = Message(0x222, 2, mux, selected);
        var result = new CanDecodingService().Decode([Frame(0, 0x222, [1, 77])], new DbcDatabase { Messages = [message] }, null, CancellationToken.None);
        Assert.Equal(2, result.Samples.Count);
        Assert.Contains(result.Samples, sample => sample.SignalName == "Selected" && sample.Value == 77);
        (result.Samples as IDisposable)?.Dispose();
    }

    [Fact]
    public void Decoder_LengthMismatchCreatesNoArtificialSamples()
    {
        var message = Message(0x123, 8, Signal("A", 0, 8));
        var result = new CanDecodingService().Decode([Frame(0, 0x123, [42])], new DbcDatabase { Messages = [message] }, null, CancellationToken.None);
        Assert.Empty(result.Samples);
        Assert.Equal(1, result.Diagnostics.DecodeErrorFrameCount);
        (result.Samples as IDisposable)?.Dispose();
    }

    [Fact]
    public void Decoder_AmbiguousJ1939FallbackDoesNotGuess()
    {
        const uint dbcId1 = 0x18FF5001;
        const uint dbcId2 = 0x18FF5002;
        var signal = Signal("A", 0, 8);
        var db = new DbcDatabase { Messages = [ExtendedMessage(dbcId1, signal), ExtendedMessage(dbcId2, signal)] };
        var frame = new RawCanFrame(0L, 0x18FF50E5, 1, [1], "Rx", "1", true, 0, 1);
        var result = new CanDecodingService().Decode([frame], db, null, CancellationToken.None);
        Assert.Empty(result.Samples);
        Assert.Equal(1, result.Diagnostics.AmbiguousFrameCount);
        (result.Samples as IDisposable)?.Dispose();
    }

    [Fact]
    public void DatasetBuilder_SeparatesIdenticalIdsAndTimesAcrossChannels()
    {
        var identity1 = new SignalIdentity("1", CanFrameFormat.Classic, false, 0x100, "M", "S");
        var identity2 = identity1 with { Channel = "2" };
        var samples = new[]
        {
            new DecodedSignalSample(10, 0, 1, identity1, 1, BigInteger.One),
            new DecodedSignalSample(10, 1, 2, identity2, 2, new BigInteger(2))
        };
        using var dataset = new DatasetBuilder().Build(
            [Frame(0, 0x100, [1]) with { Channel = "1" }, Frame(0, 0x100, [2]) with { Channel = "2", FrameIndex = 1 }],
            samples, [], new DecoderDiagnostics(0, 0, 1, 0, 0, ""));
        Assert.Equal(2, dataset.SignalCount);
        Assert.Equal(2, dataset.SignalSeriesByIdentity.Count);
        Assert.Equal(1d, dataset.SignalSeriesByIdentity[identity1].Value[0]);
        Assert.Equal(2d, dataset.SignalSeriesByIdentity[identity2].Value[0]);
    }

    [Fact]
    public void HistogramCountsAlwaysAddToSampleCount_AndNoOverlapReturnsNoPairSamples()
    {
        var service = new JoystickAnalyticsService();
        var values = new[] { -10d, -1d, 0d, 1d, 10d, 10d };
        var series = new SignalSeries("A", Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray(), values);
        var single = service.AnalyzeSignal(series, histogramBins: 8);
        Assert.Equal(values.Length, single.Histogram.Sum(bin => bin.Count));
        Assert.Equal(values.Min(), single.Histogram[0].Start);
        Assert.Equal(values.Max(), single.Histogram[^1].End);

        var left = new SignalSeries("L", [0d, 1d], [0d, 1d]);
        var right = new SignalSeries("R", [2d, 3d], [0d, 1d]);
        Assert.Equal(0, service.AnalyzePair(left, right).Statistics.SampleCount);
    }

    [Fact]
    public void DuplicateTimestampsAreSkippedForSlopeInsteadOfInventingDt()
    {
        var series = new SignalSeries("A", [0d, 0d, 1d], [0d, 1000d, 1001d]);
        var result = new JoystickAnalyticsService().AnalyzeSignal(series);
        Assert.Equal(1d, result.EventStatistics.MaximumAbsoluteSlope, 12);
    }

    [Fact]
    public void AlignmentGapBeyondFiveTimesSlowMedianInvalidatesPair()
    {
        var left = new SignalSeries("L", [0d, 1d, 2d, 100d], [0d, 1d, 2d, 3d]);
        var right = new SignalSeries("R", [0d, 1d, 2d, 100d], [0d, 1d, 2d, 3d]);
        var result = new JoystickAnalyticsService().AnalyzePair(left, right);
        Assert.Equal(AnalysisStatus.GapExceeded, result.Alignment.Status);
        Assert.Equal(0, result.Statistics.SampleCount);
        Assert.True(result.Alignment.MaximumSampleDistanceSeconds >= 98d);
    }

    [Fact]
    public void FirstResponseDelayInterpolatesThresholdCrossingBetweenSourceSamples()
    {
        var command = new SignalSeries("Command", [0d, 1d, 2d], [0d, 1d, 1d]);
        var response = new SignalSeries("Response", [0d, 1d, 2d], [0d, 0d, 1d]);
        var result = new JoystickAnalyticsService().AnalyzeFirstResponseDelay(command, response, 2d, 0.5d, 10);
        Assert.InRange(result.MeanDeadTimeSeconds!.Value, 0.48d, 0.50d);
    }

    [Fact]
    public void DiskBackedSampleStoreProvidesCompactFrameLookup()
    {
        var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 0x123, "M", "S");
        using var store = new DiskBackedDecodedSampleStore();
        store.Append(new DecodedSignalSample(0, 10, 20, identity, 1, BigInteger.One));
        store.Append(new DecodedSignalSample(0, 10, 20, identity with { SignalName = "T" }, 2, new BigInteger(2)));
        store.Append(new DecodedSignalSample(1, 12, 22, identity, 3, new BigInteger(3)));
        store.Complete();
        Assert.True(store.TryGetFrameSummary(10, out var messageName, out var count));
        Assert.Equal("M", messageName);
        Assert.Equal(2, count);
        Assert.Equal([BigInteger.One, new BigInteger(2)], store.GetFrameSamples(10).Select(sample => sample.RawValue));
        Assert.False(store.TryGetFrameSummary(11, out _, out _));
    }

    [Fact]
    public async Task DbcLoader_PreservesDuplicateMuxSignalNamesAndSuppressesOverlappingMessages()
    {
        var path = await WriteTempAsync(
            "VERSION \"\"\n" +
            "NS_ :\n" +
            "BS_:\n" +
            "BU_: Vector__XXX\n" +
            "BO_ 635 Display_Data: 8 Vector__XXX\n" +
            " SG_ Display_Mode_Mux M : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n" +
            " SG_ Display_Text_Char0 m0 : 8|8@1+ (1,0) [0|255] \"\" Vector__XXX\n" +
            " SG_ Display_Text_Char0 m128 : 8|8@1+ (1,0) [0|255] \"\" Vector__XXX\n" +
            " SG_ Display_SOC_Bars m136 : 8|16@1+ (1,0) [0|65535] \"\" Vector__XXX\n" +
            "BO_ 518 Overlap_Command: 8 Vector__XXX\n" +
            " SG_ CommandAll : 0|16@1+ (1,0) [0|65535] \"\" Vector__XXX\n" +
            " SG_ Cmd_SwitchOn : 0|1@1+ (1,0) [0|1] \"\" Vector__XXX\n");
        try
        {
            var database = await new DbcLoader().LoadAsync(path, CancellationToken.None);
            var display = Assert.Single(database.Messages, message => message.Name == "Display_Data");
            Assert.Equal(2, display.Signals.Count(signal => signal.Name == "Display_Text_Char0"));
            Assert.Contains(display.Signals, signal => signal.Name == "Display_Text_Char0" && signal.MultiplexerIds.SequenceEqual([0]));
            Assert.Contains(display.Signals, signal => signal.Name == "Display_Text_Char0" && signal.MultiplexerIds.SequenceEqual([128]));
            Assert.False(display.SuppressDecoding);

            var overlap = Assert.Single(database.Messages, message => message.Name == "Overlap_Command");
            Assert.True(overlap.SuppressDecoding);
            Assert.Contains(database.Issues, issue => issue.Code == "DBC_SIGNAL_LAYOUT");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RawFrameFilterPagesWithoutMutatingOrTruncatingSource()
    {
        var frames = Enumerable.Range(0, 20).Select(index => Frame(index, 0x100, [(byte)index]) with { FrameIndex = index }).ToArray();
        var page = new RawFrameFilterService().Apply(frames, new RawFrameFilterOptions { Offset = 5, MaxRows = 4 });
        Assert.Equal([5L, 6L, 7L, 8L], page.Select(frame => frame.FrameIndex));
        Assert.Equal(20, frames.Length);
    }

    [Fact]
    public async Task CsvExportPreservesRoundTripValueNanosecondsRawAndProvenance()
    {
        var identity = new SignalIdentity("can1", CanFrameFormat.Classic, false, 0x123, "M", "S");
        var raw = BigInteger.Parse("18446744073709551615", CultureInfo.InvariantCulture);
        var frame = Frame(123_456_789, 0x123, [0xFF]) with { SourceLineNumber = 7 };
        var report = new ImportReport("test", 1, 0, 1, 0, []);
        using var dataset = new DatasetBuilder().Build([frame],
            [new DecodedSignalSample(frame.TimestampNanoseconds, 0, 7, identity, 0.10000000000000002d, raw, "u")],
            [], new DecoderDiagnostics(0, 0, 1, 0, 0, ""), report, DatasetCompleteness.Complete, "AA", "BB", "2.0.0");
        var path = Path.GetTempFileName();
        try
        {
            await new CsvExportService().ExportDecodedSignalsAsync(path, dataset, CancellationToken.None);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("time_ns", text);
            Assert.Contains("123456789", text);
            Assert.Contains("18446744073709551615", text);
            Assert.Contains("0.10000000000000002", text);
            Assert.Contains(",AA,BB,2.0.0", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DiskBackedFrameStoreDoesNotTruncateAndPreservesSourceOrder()
    {
        using var store = new DiskBackedFrameStore();
        for (var i = 0; i < 100_000; i++) store.Append(Frame(i, 0x123, [(byte)i]) with { FrameIndex = i, SourceLineNumber = i + 1 });
        store.Complete();
        Assert.Equal(100_000, store.Count);
        Assert.Equal(99_999, store[^1].FrameIndex);
        Assert.Equal(Enumerable.Range(0, 100_000).Select(i => (long)i), store.Select(frame => frame.FrameIndex));
    }

    private static RawCanFrame Frame(long timestamp, uint id, byte[] data) =>
        new(timestamp, id, (byte)data.Length, data, "Rx", "1", id > 0x7FF, 0, 1);

    private static DbcMessage Message(uint id, int dlc, params DbcSignal[] signals)
    {
        var message = new DbcMessage { RawFrameId = id, IsExtendedFrame = false, Name = "M", Dlc = dlc };
        message.Signals.AddRange(signals);
        return message;
    }

    private static DbcMessage ExtendedMessage(uint id, DbcSignal signal)
    {
        var message = new DbcMessage { RawFrameId = id | 0x80000000, IsExtendedFrame = true, Name = $"M{id:X}", Dlc = 1 };
        message.Signals.Add(signal);
        return message;
    }

    private static DbcSignal Signal(
        string name,
        int startBit,
        int length,
        double scale = 1,
        bool isMultiplexer = false,
        IReadOnlyList<int>? multiplexerIds = null,
        bool littleEndian = true,
        bool signed = false,
        DbcSignalValueKind valueKind = DbcSignalValueKind.Integer) => new()
    {
        Name = name,
        StartBit = startBit,
        Length = length,
        IsLittleEndian = littleEndian,
        IsSigned = signed,
        Scale = scale,
        Offset = 0,
        Minimum = 0,
        Maximum = double.MaxValue,
        Unit = string.Empty,
        IsMultiplexer = isMultiplexer,
        MultiplexerIds = multiplexerIds ?? [],
        ValueKind = valueKind
    };

    private static async Task<string> WriteTempAsync(string content)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, content);
        return path;
    }
}
