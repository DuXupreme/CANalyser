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

public sealed class BranchBehaviorTests
{
    [Fact]
    public async Task Pipeline_StrictDecodeErrorBlocksAndPartialCarriesPermanentStatus()
    {
        var logPath = Path.GetTempFileName();
        var dbcPath = Path.Combine(Path.GetTempPath(), $"mismatch_{Guid.NewGuid():N}.dbc");
        await File.WriteAllTextAsync(logPath, "(1.0) can1 123#2A\n");
        var message = new DbcMessage { RawFrameId = 0x123, IsExtendedFrame = false, Name = "M", Dlc = 2 };
        message.Signals.Add(Signal("S", 0, 8));
        await new DbcWriter().WriteAsync(new DbcDatabase { Messages = [message] }, dbcPath, CancellationToken.None);
        var pipeline = CreatePipeline();
        try
        {
            var error = await Assert.ThrowsAsync<ImportIntegrityException>(() =>
                pipeline.LoadAsync(logPath, dbcPath, ImportMode.Strict, null, CancellationToken.None));
            Assert.Contains(error.Report.Issues, issue => issue.Code == "DECODE_ERROR");

            using var partial = await pipeline.LoadAsync(logPath, dbcPath, ImportMode.Partial, null, CancellationToken.None);
            Assert.Equal(DatasetCompleteness.Partial, partial.Completeness);
            Assert.Empty(partial.DecodedSamples);
            Assert.Equal(1, partial.Diagnostics.DecodeErrorFrameCount);
        }
        finally
        {
            File.Delete(logPath);
            File.Delete(dbcPath);
        }
    }

    [Fact]
    public async Task ParsingProbesRejectUnknownInputAndAcceptPeakTsvFd()
    {
        var service = CreateParsingService();
        var unknown = await TempAsync("this is not a can log\n");
        var peak = await TempAsync(";$FILEVERSION=2.1\n1.5\t2\tx\t18FF50E5\t9\t00 01 02 03 04 05 06 07 08 09 0A 0B\tRx FD x\n");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ParseAsync(unknown, ImportMode.Strict, null, CancellationToken.None));
            var result = await new PeakTrcParser().ParseAsync(peak, ImportMode.Partial, null, CancellationToken.None);
            Assert.NotNull(result);
            var frame = Assert.Single(result!.Frames);
            Assert.Equal(CanFrameFormat.FlexibleDataRate, frame.FrameFormat);
            Assert.True(frame.IsExtended);
            Assert.Equal(9, frame.Dlc);
            Assert.Equal(12, frame.PayloadLength);
            (result.Frames as IDisposable)?.Dispose();
        }
        finally
        {
            File.Delete(unknown);
            File.Delete(peak);
        }
    }

    [Theory]
    [InlineData("25:00:00:0")]
    [InlineData("00:60:00:0")]
    [InlineData("00:00:60:0")]
    [InlineData("bad")]
    public void InvalidClockTimestampsAreRejected(string timestamp) =>
        Assert.ThrowsAny<Exception>(() => TimestampParsers.ParseBusmasterTimeOfDayToNanoseconds(timestamp));

    [Theory]
    [InlineData("ABC", 0xABC)]
    [InlineData("0x123", 0x123)]
    [InlineData("291", 291)]
    public void HexUtilitiesParsesSupportedIdRepresentations(string token, uint expected) =>
        Assert.Equal(expected, HexUtilities.ParseIntAuto(token));

    [Fact]
    public void HexUtilitiesRejectsOddAndInvalidPayloadAndNormalizesSearchToken()
    {
        Assert.Throws<FormatException>(() => HexUtilities.ParseHexPayload("ABC"));
        Assert.Throws<FormatException>(() => HexUtilities.ParseHexPayload("GG"));
        Assert.Null(HexUtilities.ParseDataBytes(["AA", "GG"]));
        Assert.Equal([0xAA, 0xBB], HexUtilities.ParseDataBytes(["AA", "BB"]));
        Assert.Equal("AABB", HexUtilities.NormalizeHexContainsToken("0xAA bb"));
        Assert.Equal(string.Empty, HexUtilities.NormalizeHexContainsToken(" "));
    }

    [Fact]
    public void AnalyticsEdgeCasesReportRisingFallingEmptyAndResourceLimit()
    {
        var service = new JoystickAnalyticsService();
        var signal = new SignalSeries("steps", [0d, 1d, 2d, 3d, 4d], [-1d, 1d, 1d, -1d, -1d]);
        var analytics = service.AnalyzeSignal(signal, 8, 0, 0.5, 8);
        Assert.Equal(1, analytics.EventStatistics.RisingCrossings);
        Assert.Equal(1, analytics.EventStatistics.FallingCrossings);
        Assert.True(analytics.EventStatistics.TimeAboveThresholdPercent > 0);
        Assert.True(analytics.EventStatistics.FlatlinePercent > 0);

        var empty = new SignalSeries("empty", [], []);
        Assert.Equal(0, service.AnalyzeSignal(empty).Statistics.SampleCount);
        Assert.Empty(service.RankSignals(new Dictionary<string, SignalSeries> { [empty.Label] = empty }));
        Assert.Equal(AnalysisStatus.InsufficientOverlap, service.AnalyzePair(empty, signal).Alignment.Status);
        Assert.Equal(0, service.AnalyzeDelay(empty, signal).SampleCount);
        Assert.Equal(0, service.AnalyzeFirstResponseDelay(empty, signal).MatchedReactionCount);

        var fine = new SignalSeries("fine", [0d, 0.01d, 0.02d, 100d], [0d, 1d, 2d, 3d]);
        var broad = new SignalSeries("broad", [0d, 0.01d, 0.02d, 100d], [0d, 1d, 2d, 3d]);
        Assert.Throws<InvalidOperationException>(() => service.AnalyzeDelay(fine, broad, 1d));
    }

    [Fact]
    public void PlotPresetV2RoundTripsAndRejectsWrongTypeOrEmptyJson()
    {
        var group = PlotGroup.Create("P", ["A", "A", "B"], new Dictionary<string, double> { ["A"] = 1.25 });
        Assert.Equal(["A", "B"], group.Signals);
        var preset = new PlotPreset { Version = 2, PlotGroups = [group] };
        var serializer = new PresetSerializer();
        var loaded = serializer.Deserialize(serializer.Serialize(preset));
        Assert.Equal(2, loaded.Version);
        Assert.Equal(1.25, loaded.PlotGroups[0].Offsets["A"]);
        Assert.Throws<InvalidDataException>(() => serializer.Deserialize("null"));
        Assert.Throws<InvalidDataException>(() => serializer.Deserialize("{\"preset_type\":\"wrong\"}"));
    }

    [Fact]
    public void DecoderHandlesExtendedMuxRangeSignedFloat32AndInvalidSignalBounds()
    {
        var mux = Signal("Mux", 0, 8, isMultiplexer: true);
        var ranged = Signal("Ranged", 8, 8, ranges: [new DbcMultiplexerRange("Mux", 2, 4)]);
        var invalid = Signal("Invalid", 80, 8);
        var message = new DbcMessage { RawFrameId = 0x123, IsExtendedFrame = false, Name = "M", Dlc = 2 };
        message.Signals.AddRange([mux, ranged]);
        var decoder = new CanDecodingService();
        var selected = decoder.Decode([new RawCanFrame(0L, 0x123, 2, [3, 9], "Rx", "1", false, 0, 1)],
            new DbcDatabase { Messages = [message] }, null, CancellationToken.None);
        Assert.Contains(selected.Samples, sample => sample.SignalName == "Ranged");
        (selected.Samples as IDisposable)?.Dispose();

        message.Signals.Add(invalid);
        var failed = decoder.Decode([new RawCanFrame(0L, 0x123, 2, [3, 9], "Rx", "1", false, 0, 1)],
            new DbcDatabase { Messages = [message] }, null, CancellationToken.None);
        Assert.Empty(failed.Samples);
        Assert.Equal(1, failed.Diagnostics.DecodeErrorFrameCount);
        (failed.Samples as IDisposable)?.Dispose();

        var floatMessage = new DbcMessage { RawFrameId = 0x124, IsExtendedFrame = false, Name = "F", Dlc = 4 };
        floatMessage.Signals.Add(Signal("Float", 0, 32, valueKind: DbcSignalValueKind.IeeeFloat32));
        var floatResult = decoder.Decode([new RawCanFrame(0L, 0x124, 4, BitConverter.GetBytes(2.5f), "Rx", "1", false, 0, 1)],
            new DbcDatabase { Messages = [floatMessage] }, null, CancellationToken.None);
        Assert.Equal(2.5d, Assert.Single(floatResult.Samples).Value);
        (floatResult.Samples as IDisposable)?.Dispose();
    }

    [Theory]
    [InlineData(0x800u, false, 8, 8, false, false, 0)]
    [InlineData(0x20000000u, true, 8, 8, false, false, 0)]
    [InlineData(0x123u, false, 8, 65, false, false, 0)]
    [InlineData(0x123u, false, 8, 7, false, false, 0)]
    [InlineData(0x123u, false, 8, 8, false, true, 8)]
    [InlineData(0x123u, false, 9, 9, true, false, 0)]
    [InlineData(0x123u, false, 9, 12, true, true, 9)]
    [InlineData(0x123u, false, 10, 12, true, false, 0)]
    [InlineData(0x123u, false, 12, 24, true, true, 12)]
    [InlineData(0x123u, false, 64, 64, true, true, 15)]
    [InlineData(0x123u, false, 63, 64, true, false, 0)]
    public void CanFrameValidationCoversClassicAndFdMappings(
        uint id, bool extended, int declared, int payload, bool fd, bool expected, byte expectedDlc)
    {
        var valid = CanFrameValidation.TryNormalize(id, extended, declared, payload, fd,
            out var dlc, out var format, out var error);
        Assert.Equal(expected, valid);
        if (expected)
        {
            Assert.Equal(expectedDlc, dlc);
            Assert.Equal(fd ? CanFrameFormat.FlexibleDataRate : CanFrameFormat.Classic, format);
            Assert.Equal(string.Empty, error);
        }
        else
        {
            Assert.NotEmpty(error);
        }
    }

    [Fact]
    public void EventDelayAnalysisCoversRisingFallingMatchedAndUnmatchedEdges()
    {
        var time = Enumerable.Range(0, 120).Select(index => index * 0.01d).ToArray();
        var command = new double[time.Length];
        var response = new double[time.Length];
        for (var i = 0; i < time.Length; i++)
        {
            command[i] = i is >= 20 and < 55 or >= 80 ? 1d : 0d;
            response[i] = i is >= 24 and < 60 or >= 85 ? 1d : 0d;
        }

        var service = new JoystickAnalyticsService();
        var commandSeries = new SignalSeries("command", time, command);
        var responseSeries = new SignalSeries("response", time, response);
        var delay = service.AnalyzeDelay(commandSeries, responseSeries, 0.2d, 0.3d, 1000);
        Assert.True(delay.MatchedDelayEvents >= 2);
        Assert.True(delay.RisingDelay.MatchedEventCount > 0);
        Assert.True(delay.FallingDelay.MatchedEventCount > 0);
        var first = service.AnalyzeFirstResponseDelay(commandSeries, responseSeries, 0.2d, 0.1d, 12);
        Assert.True(first.CommandEdgeCount >= 3);
        Assert.True(first.RisingDeadTime.MatchedEventCount > 0);
        Assert.True(first.FallingDeadTime.MatchedEventCount > 0);

        var constantResponse = new SignalSeries("constant", time, new double[time.Length]);
        var unmatched = service.AnalyzeFirstResponseDelay(commandSeries, constantResponse, 0.03d, 0.1d, 12);
        Assert.True(unmatched.CommandEdgeCount > 0);
        Assert.Equal(0, unmatched.MatchedReactionCount);
    }

    [Fact]
    public async Task PipelineAmbiguousJ1939StrictBlocksAndPartialDoesNotGuess()
    {
        var logPath = await TempAsync("(1.0) can1 18FF50E5#01\n");
        var dbcPath = Path.Combine(Path.GetTempPath(), $"ambiguous_{Guid.NewGuid():N}.dbc");
        var one = new DbcMessage { RawFrameId = 0x18FF5001 | 0x80000000, IsExtendedFrame = true, Name = "One", Dlc = 1 };
        var two = new DbcMessage { RawFrameId = 0x18FF5002 | 0x80000000, IsExtendedFrame = true, Name = "Two", Dlc = 1 };
        one.Signals.Add(Signal("S", 0, 8));
        two.Signals.Add(Signal("S", 0, 8));
        await new DbcWriter().WriteAsync(new DbcDatabase { Messages = [one, two] }, dbcPath, CancellationToken.None);
        var pipeline = CreatePipeline();
        try
        {
            var error = await Assert.ThrowsAsync<ImportIntegrityException>(() =>
                pipeline.LoadAsync(logPath, dbcPath, ImportMode.Strict, null, CancellationToken.None));
            Assert.Contains(error.Report.Issues, issue => issue.Code == "AMBIGUOUS_J1939");
            using var partial = await pipeline.LoadAsync(logPath, dbcPath, ImportMode.Partial, null, CancellationToken.None);
            Assert.Equal(DatasetCompleteness.Partial, partial.Completeness);
            Assert.Equal(1, partial.Diagnostics.AmbiguousFrameCount);
            Assert.Empty(partial.DecodedSamples);
        }
        finally
        {
            File.Delete(logPath);
            File.Delete(dbcPath);
        }
    }

    [Fact]
    public async Task ParserErrorMatricesClassifyEveryLineWithoutHidingDiagnostics()
    {
        var busmaster = await TempAsync(
            "*** header\n" +
            "12:00:00:000000002 Rx 1 123 d 1 AA\n" +
            "12:00:00:000000001 Tx 1 123 d 1 BB\n" +
            "25:00:00:000000000 Rx 1 123 d 1 AA\n" +
            "12:00:01:0 Rx 1 123 d 2 AA\n" +
            "invalid\n");
        var css = await TempAsync(
            "# comment\npre-header text\nTimestamp;Type;ID;Data\n" +
            "01T000000001;0;123;AA\n01T000000002;1;18FF50E5;BB\n" +
            "01T000000003;4;123;AA\n01T000000004;0;123;ABC\nbad;columns\n");
        var candump = await TempAsync("\n# comment\n(1.0) can0 123#AA\n(2.0) can0 123##\n(3.0) can0 123#ABC\ninvalid\n");
        var peak = await TempAsync(";$FILEVERSION=2.1\n1) 1.0 Rx 123 1 AA\n2) 2.0 Rx 123 2 AA\nnot peak\n");
        try
        {
            var busResult = await new BusmasterParser().ParseAsync(busmaster, ImportMode.Partial, null, CancellationToken.None);
            var cssResult = await new CssSemicolonParser().ParseAsync(css, ImportMode.Partial, null, CancellationToken.None);
            var canResult = await new CandumpParser().ParseAsync(candump, ImportMode.Partial, null, CancellationToken.None);
            var peakResult = await new PeakTrcParser().ParseAsync(peak, ImportMode.Partial, null, CancellationToken.None);
            foreach (var result in new[] { busResult, cssResult, canResult, peakResult })
            {
                Assert.NotNull(result);
                Assert.True(result!.Report.IsConsistent);
                Assert.True(result.Report.RejectedLines > 0);
                Assert.NotEmpty(result.Report.Issues);
                (result.Frames as IDisposable)?.Dispose();
            }

            Assert.Contains(busResult!.Report.Issues, issue => issue.Code == "TIME_BACKWARDS");
        }
        finally
        {
            File.Delete(busmaster);
            File.Delete(css);
            File.Delete(candump);
            File.Delete(peak);
        }
    }

    [Fact]
    public void DiskStoresEnforceAppendOnlyBoundsCompletionAndDisposal()
    {
        var neverCompletedFrames = new DiskBackedFrameStore();
        neverCompletedFrames.Dispose();
        var neverCompletedSamples = new DiskBackedDecodedSampleStore();
        neverCompletedSamples.Dispose();

        var frames = new DiskBackedFrameStore();
        frames.Append(new RawCanFrame(0L, 0x123, 1, [1], "Rx", "1", false, 0, 1));
        frames.Complete();
        frames.Complete();
        Assert.Single(frames);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = frames[1]);
        Assert.Throws<InvalidOperationException>(() => frames.Append(new RawCanFrame(1L, 0x123, 1, [2], "Rx", "1", false, 1, 2)));
        frames.Dispose();
        frames.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = frames[0]);

        var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 0x123, "M", "S");
        var samples = new DiskBackedDecodedSampleStore();
        samples.Append(new DecodedSignalSample(0, 0, 1, identity, 1, BigInteger.One));
        samples.Complete();
        samples.Complete();
        Assert.Single(samples.GetFrameSamples(0));
        Assert.Empty(samples.GetFrameSamples(999));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = samples[1]);
        Assert.Throws<InvalidOperationException>(() => samples.Append(new DecodedSignalSample(1, 1, 2, identity, 2, new BigInteger(2))));
        samples.Dispose();
        samples.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = samples[0]);
    }

    [Fact]
    public void PartialQualityViewAndLazySeriesCoverFallbackLookupAndStableSort()
    {
        var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 0x123, "M", "S");
        var source = new[]
        {
            new DecodedSignalSample(20, 2, 3, identity, 2, new BigInteger(2)),
            new DecodedSignalSample(10, 1, 2, identity, 1, BigInteger.One)
        };
        using var dataset = new DatasetBuilder().Build(
            [new RawCanFrame(10, 0x123, 1, [1], "Rx", "1", false, 1, 2)], source, [],
            new DecoderDiagnostics(0, 0, 1, 0, 0, ""), completeness: DatasetCompleteness.Partial);
        Assert.All(dataset.DecodedSamples, sample => Assert.Equal(DecodeQuality.PartialDataset, sample.Quality));
        var lookup = Assert.IsAssignableFrom<CanAnalyzer.Core.Interfaces.IFrameSampleLookup>(dataset.DecodedSamples);
        Assert.True(lookup.TryGetFrameSummary(2, out var message, out var count));
        Assert.Equal("M", message);
        Assert.Equal(1, count);
        Assert.False(lookup.TryGetFrameSummary(999, out _, out _));
        Assert.Equal([10L, 20L], dataset.SignalSeriesByIdentity[identity].TimestampNanoseconds);
        Assert.Equal([1d, 2d], dataset.SignalSeriesByIdentity[identity].Value);
    }

    [Fact]
    public void DbcWriterSanitizesIdentifiersNumbersUnitsAndMultipleMuxIds()
    {
        var mux = Signal("", 0, 8, isMultiplexer: true);
        var value = Signal("1 bad-name", 8, 8);
        value = new DbcSignal
        {
            Name = value.Name, StartBit = value.StartBit, Length = value.Length, IsLittleEndian = true,
            IsSigned = false, Scale = double.NaN, Offset = double.PositiveInfinity, Minimum = 0, Maximum = 1,
            Unit = "a\"b", MultiplexerIds = [1, 3]
        };
        var message = new DbcMessage { RawFrameId = 0x123, IsExtendedFrame = false, Name = "1 bad message", Dlc = 2 };
        message.Signals.AddRange([mux, value]);
        var text = new DbcWriter().Serialize(new DbcDatabase { Messages = [message] });
        Assert.Contains("BO_ 291 _1_bad_message", text);
        Assert.Contains("SG_ Signal M", text);
        Assert.Contains("SG_ _1_bad_name m1", text);
        Assert.Contains("(0,0)", text);
        Assert.Contains("\"ab\"", text);
        Assert.Contains("1-1,3-3", text);
    }

    [Fact]
    public async Task GenericParserCoversDirectionFdAndInvalidCandidateBranches()
    {
        var fdBytes = string.Join(' ', Enumerable.Range(0, 12).Select(index => index.ToString("X2")));
        var path = await TempAsync(
            $"0.000000001 Tx 18FF50E5 12 {fdBytes}\n" +
            "0.1 Rx 123 1 AA\n" +
            "0.2 Rx ZZ 1 AA\n" +
            "0.3 123 -1 AA\n" +
            "too short\n");
        try
        {
            var result = await new GenericTextCanParser().ParseAsync(path, ImportMode.Partial, null, CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(2, result!.Frames.Count);
            Assert.Equal(CanFrameDirection.Transmit, result.Frames[0].Direction);
            Assert.Equal(CanFrameFormat.FlexibleDataRate, result.Frames[0].FrameFormat);
            Assert.Equal(CanFrameDirection.Receive, result.Frames[1].Direction);
            Assert.Equal(3, result.Report.RejectedLines);
            Assert.True(result.Report.IsConsistent);
            (result.Frames as IDisposable)?.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DecoderCoversCandidateFallbackUnmatchedMuxSkipsAndNonFiniteFloat()
    {
        var wrongLength = new DbcMessage { RawFrameId = 0x200, IsExtendedFrame = false, Name = "Wrong", Dlc = 2 };
        wrongLength.Signals.Add(Signal("A", 0, 8));
        var correct = new DbcMessage { RawFrameId = 0x200, IsExtendedFrame = false, Name = "Correct", Dlc = 1 };
        correct.Signals.Add(Signal("A", 0, 8));

        var muxMessage = new DbcMessage { RawFrameId = 0x201, IsExtendedFrame = false, Name = "Muxed", Dlc = 2 };
        muxMessage.Signals.Add(Signal("Mux", 0, 8, isMultiplexer: true));
        var conditional = Signal("OnlyTwo", 8, 8);
        conditional = new DbcSignal
        {
            Name = conditional.Name, StartBit = conditional.StartBit, Length = conditional.Length,
            IsLittleEndian = true, IsSigned = false, Scale = 1, Offset = 0, Minimum = 0, Maximum = 255,
            Unit = string.Empty, MultiplexerIds = [2]
        };
        muxMessage.Signals.Add(conditional);
        muxMessage.Signals.Add(Signal("MissingMuxRange", 8, 8, ranges: [new DbcMultiplexerRange("OtherMux", 0, 10)]));

        var floatMessage = new DbcMessage { RawFrameId = 0x202, IsExtendedFrame = false, Name = "Float", Dlc = 4 };
        floatMessage.Signals.Add(Signal("NaN", 0, 32, valueKind: DbcSignalValueKind.IeeeFloat32));
        var invalidMessage = new DbcMessage { RawFrameId = 0x203, IsExtendedFrame = false, Name = "Invalid", Dlc = 1 };
        invalidMessage.Signals.Add(Signal("Zero", 0, 0));

        var frames = new RawCanFrame[]
        {
            new(0L, 0x200, 1, [42], "Rx", "1", false, 0, 1),
            new(1L, 0x201, 2, [1, 99], "Rx", "1", false, 1, 2),
            new(2L, 0x202, 4, BitConverter.GetBytes(float.NaN), "Rx", "1", false, 2, 3),
            new(3L, 0x203, 1, [0], "Rx", "1", false, 3, 4),
            new(4L, 0x777, 1, [0], "Rx", "1", false, 4, 5)
        };
        var result = new CanDecodingService().Decode(frames,
            new DbcDatabase { Messages = [wrongLength, correct, muxMessage, floatMessage, invalidMessage] },
            null, CancellationToken.None);
        Assert.Contains(result.Samples, sample => sample.MessageName == "Correct" && sample.Value == 42);
        Assert.Contains(result.Samples, sample => sample.SignalName == "Mux");
        Assert.DoesNotContain(result.Samples, sample => sample.SignalName is "OnlyTwo" or "MissingMuxRange" or "NaN");
        Assert.Equal(1, result.Diagnostics.UnmatchedFrameCount);
        Assert.Equal(1, result.Diagnostics.DecodeErrorFrameCount);
        Assert.Contains("Top onbekende", result.Diagnostics.DecodeNote);
        Assert.Contains("strict niet konden", result.Diagnostics.DecodeNote);
        (result.Samples as IDisposable)?.Dispose();
    }

    [Fact]
    public void PairAnalyticsCoversQuadrantsSinglePointDuplicateTimeAndReduction()
    {
        var service = new JoystickAnalyticsService();
        var time = Enumerable.Range(0, 500).Select(index => index * 0.01d).ToArray();
        var x = time.Select((_, index) => index % 4 is 0 or 3 ? 1d : -1d).ToArray();
        var y = time.Select((_, index) => index % 4 is 0 or 1 ? 1d : -1d).ToArray();
        var result = service.AnalyzePair(new SignalSeries("x", time, x), new SignalSeries("y", time, y), 0.1, 0.9, 8, 100);
        Assert.InRange(result.PathPoints.Count, 2, 102);
        Assert.True(result.Statistics.Quadrant1Percent > 0);
        Assert.True(result.Statistics.Quadrant2Percent > 0);
        Assert.True(result.Statistics.Quadrant3Percent > 0);
        Assert.True(result.Statistics.Quadrant4Percent > 0);

        var one = service.AnalyzePair(new SignalSeries("one-x", [1d], [2d]), new SignalSeries("one-y", [1d], [3d]));
        Assert.Equal(1, one.Statistics.SampleCount);
        var duplicate = service.AnalyzePair(
            new SignalSeries("dup-x", [0d, 0d, 1d], [0d, 1d, 2d]),
            new SignalSeries("dup-y", [0d, 0d, 1d], [0d, -1d, -2d]));
        Assert.True(duplicate.Alignment.DuplicateTimestampIntervalsSkipped > 0);
    }

    [Fact]
    public void PresetAndDomainEdgeBranchesAreExplicit()
    {
        var serializer = new PresetSerializer();
        Assert.Throws<InvalidDataException>(() => serializer.Deserialize("{\"preset_type\":\"can-log-viewer-layout\",\"version\":3}"));
        var normalized = serializer.Deserialize("{\"preset_type\":\"can-log-viewer-layout\",\"version\":2,\"plot_groups\":null,\"view\":null}");
        Assert.NotNull(normalized.PlotGroups);
        Assert.NotNull(normalized.View);
        var emptyGroup = PlotGroup.Create();
        Assert.Empty(emptyGroup.Signals);
        Assert.Empty(emptyGroup.Offsets);

        var frame = new RawCanFrame(0L, 0x123, 3, [0, 32, 126], "", "", false);
        Assert.Equal(". ~", frame.DataAscii);
        Assert.Equal("00 20 7E", frame.DataHex);
    }

    private static CanAnalysisPipeline CreatePipeline() => new(
        CreateParsingService(), new DbcLoader(), new CanDecodingService(), new DatasetBuilder(),
        NullLogger<CanAnalysisPipeline>.Instance);

    private static CanLogParsingService CreateParsingService() => new(
        new CssSemicolonParser(), new BusmasterParser(), new PeakTrcParser(), new CandumpParser(),
        new GenericTextCanParser(), NullLogger<CanLogParsingService>.Instance);

    private static DbcSignal Signal(
        string name,
        int start,
        int length,
        bool isMultiplexer = false,
        IReadOnlyList<DbcMultiplexerRange>? ranges = null,
        DbcSignalValueKind valueKind = DbcSignalValueKind.Integer) => new()
    {
        Name = name, StartBit = start, Length = length, IsLittleEndian = true, IsSigned = false,
        Scale = 1, Offset = 0, Minimum = 0, Maximum = 100, Unit = string.Empty,
        IsMultiplexer = isMultiplexer, MultiplexerRanges = ranges ?? [], ValueKind = valueKind
    };

    private static async Task<string> TempAsync(string content)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, content);
        return path;
    }
}
