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
}
