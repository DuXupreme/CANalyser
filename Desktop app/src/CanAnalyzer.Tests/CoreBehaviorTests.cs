using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Parsing;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class CoreBehaviorTests
{
    [Fact]
    public async Task CssParser_ParsesRowsAndNormalizesTime()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                tempFile,
                "Timestamp;Type;ID;Data\n01T000000001;0;123;0102\n01T000000011;1;18FF50E5;A1B2C3\n");

            var parser = new CssSemicolonParser();
            var rows = await parser.ParseAsync(tempFile, null, CancellationToken.None);

            Assert.NotNull(rows);
            Assert.Equal(2, rows!.Count);
            Assert.Equal(0d, rows[0].TimeSeconds, 6);
            Assert.Equal(0.010d, rows[1].TimeSeconds, 6);
            Assert.False(rows[0].IsExtended);
            Assert.True(rows[1].IsExtended);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Downsampling_MinMax_RespectsPointBudget()
    {
        var x = Enumerable.Range(0, 10_000).Select(v => (float)v).ToArray();
        var y = x.Select(v => (float)Math.Sin(v * 0.01)).ToArray();
        var (xs, ys) = Downsampling.MinMax(x, y, 4000);

        Assert.Equal(xs.Length, ys.Length);
        Assert.True(xs.Length <= 4000 + 8);
        Assert.True(xs.Length > 100);
    }

    [Fact]
    public void Decoder_DecodeSimpleSignal()
    {
        var message = new DbcMessage
        {
            RawFrameId = 0x123,
            IsExtendedFrame = false,
            Name = "ExampleMessage",
            Dlc = 8
        };
        message.Signals.Add(
            new DbcSignal
            {
                Name = "ExampleSignal",
                StartBit = 0,
                Length = 8,
                IsLittleEndian = true,
                IsSigned = false,
                Scale = 1,
                Offset = 0,
                Minimum = 0,
                Maximum = 255,
                Unit = string.Empty
            });

        var database = new DbcDatabase { Messages = [message] };
        var rawFrames = new List<RawCanFrame>
        {
            new(0.1, 0x123, 8, [42, 0, 0, 0, 0, 0, 0, 0], "Rx", "1", false)
        };

        var decoder = new CanDecodingService();
        var result = decoder.Decode(rawFrames, database, progress: null, CancellationToken.None);

        Assert.Single(result.Samples);
        Assert.Equal("ExampleMessage", result.Samples[0].MessageName);
        Assert.Equal("ExampleSignal", result.Samples[0].SignalName);
        Assert.Equal(42f, result.Samples[0].Value);
    }

    [Fact]
    public void DbcBitLayout_ComputesIntelAndMotorolaBits()
    {
        Assert.Equal([8, 9, 10, 11], DbcBitLayout.GetOccupiedLsb0Bits(8, 4, isLittleEndian: true));

        // Motorola signal starting at bit 7 covers the first byte MSB-first.
        Assert.Equal([7, 6, 5, 4, 3, 2, 1, 0], DbcBitLayout.GetOccupiedLsb0Bits(7, 8, isLittleEndian: false));

        // Crossing a byte boundary continues at the next byte's MSB.
        Assert.Equal([7, 6, 5, 4, 3, 2, 1, 0, 15, 14], DbcBitLayout.GetOccupiedLsb0Bits(7, 10, isLittleEndian: false));
    }

    [Fact]
    public async Task DbcWriter_RoundTripsThroughLoader()
    {
        var engine = new DbcMessage
        {
            RawFrameId = 0x123,
            IsExtendedFrame = false,
            Name = "EngineData",
            Dlc = 8
        };
        engine.Signals.Add(new DbcSignal
        {
            Name = "Rpm", StartBit = 24, Length = 16, IsLittleEndian = true, IsSigned = false,
            Scale = 0.125, Offset = 0, Minimum = 0, Maximum = 8000, Unit = "rpm"
        });
        engine.Signals.Add(new DbcSignal
        {
            Name = "Temp", StartBit = 7, Length = 8, IsLittleEndian = false, IsSigned = true,
            Scale = 1, Offset = -40, Minimum = -40, Maximum = 215, Unit = "degC"
        });

        var j1939 = new DbcMessage
        {
            RawFrameId = 0x18FF50E5 | 0x80000000,
            IsExtendedFrame = true,
            Name = "J1939Msg",
            Dlc = 8
        };
        j1939.Signals.Add(new DbcSignal
        {
            Name = "Selector", StartBit = 0, Length = 8, IsLittleEndian = true, IsSigned = false,
            Scale = 1, Offset = 0, Minimum = 0, Maximum = 255, Unit = string.Empty, IsMultiplexer = true
        });
        j1939.Signals.Add(new DbcSignal
        {
            Name = "ValueA", StartBit = 8, Length = 8, IsLittleEndian = true, IsSigned = false,
            Scale = 1, Offset = 0, Minimum = 0, Maximum = 255, Unit = string.Empty, MultiplexerIds = [0]
        });

        var database = new DbcDatabase { Messages = [engine, j1939] };

        var tempFile = Path.Combine(Path.GetTempPath(), $"roundtrip_{Guid.NewGuid():N}.dbc");
        try
        {
            await new DbcWriter().WriteAsync(database, tempFile, CancellationToken.None);
            var loaded = await new DbcLoader().LoadAsync(tempFile, CancellationToken.None);

            Assert.Equal(2, loaded.Messages.Count);

            var loadedEngine = loaded.Messages.Single(m => m.Name == "EngineData");
            Assert.False(loadedEngine.IsExtendedFrame);
            Assert.Equal(0x123u, loadedEngine.NormalizedFrameId);
            Assert.Equal(8, loadedEngine.Dlc);

            var rpm = loadedEngine.Signals.Single(s => s.Name == "Rpm");
            Assert.Equal(24, rpm.StartBit);
            Assert.Equal(16, rpm.Length);
            Assert.True(rpm.IsLittleEndian);
            Assert.False(rpm.IsSigned);
            Assert.Equal(0.125, rpm.Scale, 6);
            Assert.Equal("rpm", rpm.Unit);

            var temp = loadedEngine.Signals.Single(s => s.Name == "Temp");
            Assert.False(temp.IsLittleEndian);
            Assert.True(temp.IsSigned);
            Assert.Equal(-40, temp.Offset, 6);

            var loadedJ1939 = loaded.Messages.Single(m => m.Name == "J1939Msg");
            Assert.True(loadedJ1939.IsExtendedFrame);
            Assert.Equal(0x18FF50E5u, loadedJ1939.NormalizedFrameId);
            Assert.True(loadedJ1939.Signals.Single(s => s.Name == "Selector").IsMultiplexer);
            Assert.Contains(0, loadedJ1939.Signals.Single(s => s.Name == "ValueA").MultiplexerIds);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
