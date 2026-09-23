using CanAnalyzer.Core.Decoding;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class BusmasterDbfTests
{
    [Fact]
    public async Task DbfRoundTripPreservesEditableDatabaseFields()
    {
        var standard = new DbcMessage
        {
            RawFrameId = 0x123,
            IsExtendedFrame = false,
            Name = "EngineData",
            Dlc = 8
        };
        standard.Signals.Add(new DbcSignal
        {
            Name = "Speed",
            StartBit = 0,
            Length = 16,
            IsLittleEndian = true,
            IsSigned = false,
            Scale = 0.1,
            Offset = -5,
            Minimum = 0,
            Maximum = 250,
            Unit = "km/h"
        });
        standard.Signals.Add(new DbcSignal
        {
            Name = "Temperature",
            StartBit = 23,
            Length = 16,
            IsLittleEndian = false,
            IsSigned = true,
            Scale = 1,
            Offset = -40,
            Minimum = -40,
            Maximum = 215,
            Unit = "degC"
        });

        var extended = new DbcMessage
        {
            RawFrameId = 0x18FF50E5 | 0x80000000,
            IsExtendedFrame = true,
            Name = "ExtendedMux",
            Dlc = 8
        };
        extended.Signals.Add(new DbcSignal
        {
            Name = "Mode", StartBit = 0, Length = 8, IsLittleEndian = true, IsSigned = false,
            Scale = 1, Offset = 0, Minimum = 0, Maximum = 255, Unit = string.Empty, IsMultiplexer = true
        });
        extended.Signals.Add(new DbcSignal
        {
            Name = "Value", StartBit = 8, Length = 32, IsLittleEndian = true,
            IsSigned = false, Scale = 1, Offset = 0, Minimum = -100, Maximum = 100, Unit = "V",
            MultiplexerIds = [2], ValueKind = DbcSignalValueKind.IeeeFloat32
        });

        var path = Path.Combine(Path.GetTempPath(), $"busmaster_{Guid.NewGuid():N}.dbf");
        try
        {
            await new DbcWriter().WriteAsync(
                new DbcDatabase { Messages = [standard, extended] }, path, CancellationToken.None);

            var text = await File.ReadAllTextAsync(path, System.Text.Encoding.Latin1);
            Assert.Contains("[DATABASE_VERSION] 1.3", text);
            Assert.Contains("[START_SIGNALS] Temperature,16,4,0,I", text);
            Assert.Contains(",X,Vector__XXX", text);
            Assert.Contains(",F,", text);
            Assert.Contains(",m2,", text);

            var loaded = await new DbcLoader().LoadAsync(path, CancellationToken.None);
            Assert.False(loaded.IsLosslessWritable);
            Assert.Equal(2, loaded.Messages.Count);

            var loadedStandard = loaded.Messages.Single(message => message.Name == "EngineData");
            var speed = loadedStandard.Signals.Single(signal => signal.Name == "Speed");
            Assert.True(speed.IsLittleEndian);
            Assert.Equal(0.1, speed.Scale, 10);
            Assert.Equal(-5, speed.Offset, 10);
            Assert.Equal(250, speed.Maximum, 10);

            var temperature = loadedStandard.Signals.Single(signal => signal.Name == "Temperature");
            Assert.False(temperature.IsLittleEndian);
            Assert.True(temperature.IsSigned);
            Assert.Equal(23, temperature.StartBit);
            Assert.Equal(16, temperature.Length);

            var loadedExtended = loaded.Messages.Single(message => message.Name == "ExtendedMux");
            Assert.True(loadedExtended.IsExtendedFrame);
            Assert.Equal(0x18FF50E5u, loadedExtended.NormalizedFrameId);
            Assert.True(loadedExtended.Signals.Single(signal => signal.Name == "Mode").IsMultiplexer);
            var value = loadedExtended.Signals.Single(signal => signal.Name == "Value");
            Assert.Equal(DbcSignalValueKind.IeeeFloat32, value.ValueKind);
            Assert.Contains(2, value.MultiplexerIds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DatabaseCanConvertFromDbfToDbcAndBack()
    {
        var dbfPath = Path.Combine(Path.GetTempPath(), $"convert_{Guid.NewGuid():N}.dbf");
        var dbcPath = Path.ChangeExtension(dbfPath, ".dbc");
        var convertedDbfPath = Path.Combine(Path.GetTempPath(), $"converted_{Guid.NewGuid():N}.dbf");
        const string dbf = """
            // BUSMASTER database
            [DATABASE_VERSION] 1.3
            [PROTOCOL] CAN
            [NUMBER_OF_MESSAGES] 1
            [START_MSG] Battery,512,8,1,0,S,BMS
            [START_SIGNALS] Current,16,1,0,I,1000,-1000,1,0,0.1,A,,Display
            [END_MSG]
            [NODE] BMS,Display
            """;

        try
        {
            await File.WriteAllTextAsync(dbfPath, dbf, System.Text.Encoding.Latin1);
            var fromDbf = await new DbcLoader().LoadAsync(dbfPath, CancellationToken.None);
            Assert.Empty(fromDbf.Issues);
            Assert.Equal(-100, fromDbf.Messages.Single().Signals.Single().Minimum, 10);
            Assert.Equal(100, fromDbf.Messages.Single().Signals.Single().Maximum, 10);

            var normalized = new DbcDatabase { Messages = fromDbf.Messages };
            await new DbcWriter().WriteAsync(normalized, dbcPath, CancellationToken.None);
            var fromDbc = await new DbcLoader().LoadAsync(dbcPath, CancellationToken.None);
            Assert.Equal("Battery", fromDbc.Messages.Single().Name);
            Assert.Equal("Current", fromDbc.Messages.Single().Signals.Single().Name);

            await new DbcWriter().WriteAsync(
                new DbcDatabase { Messages = fromDbc.Messages }, convertedDbfPath, CancellationToken.None);
            var roundTripped = await new DbcLoader().LoadAsync(convertedDbfPath, CancellationToken.None);
            Assert.Equal(0x200u, roundTripped.Messages.Single().NormalizedFrameId);
            Assert.Equal("A", roundTripped.Messages.Single().Signals.Single().Unit);
        }
        finally
        {
            File.Delete(dbfPath);
            File.Delete(dbcPath);
            File.Delete(convertedDbfPath);
        }
    }

    [Fact]
    public void MalformedDbfProducesTraceableImportIssue()
    {
        var database = BusmasterDbfSerializer.Parse("[START_MSG] Broken\n[START_SIGNALS] AlsoBroken");

        Assert.Empty(database.Messages);
        Assert.Contains(database.Issues, issue => issue.Code == "DBF_MESSAGE" && issue.SourceLineNumber == 1);
        Assert.Contains(database.Issues, issue => issue.Code == "DBF_SIGNAL_WITHOUT_MESSAGE" && issue.SourceLineNumber == 2);
        Assert.Contains(database.Issues, issue => issue.Code == "DBF_NO_MESSAGES");
    }
}
