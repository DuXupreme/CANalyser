using System.Globalization;
using System.Numerics;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Export;
using CanAnalyzer.Core.Utilities;
using CsvHelper;
using CsvHelper.Configuration;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class CsvExportTests
{
    private static readonly SignalIdentity A = new("can1", CanFrameFormat.Classic, false, 0x123, "Parker", "Position");
    private static readonly SignalIdentity B = A with { Channel = "can2" };
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    private static CanDataset Dataset(long origin = 10_000_000_000) => new DatasetBuilder().Build(
        [new RawCanFrame(origin, 0x123, 1, [1], "Rx", "can1", false, 0)],
        [new(origin + 1, 0, 1, B, 99, new BigInteger(99)),
         new(origin + 1001, 0, 1, A, 0.10000000000000002, BigInteger.One, "µm"),
         new(origin + 2001, 0, 1, B, 22.5, new BigInteger(22), "µm"),
         new(origin + 3001, 0, 1, A, 23.75, new BigInteger(23), "µm")],
        [], new DecoderDiagnostics(0, 0, 4, 0, 0, ""), startTimeUtc: Start);

    [Fact]
    public async Task SelectionUsesFullIdentityAndRequestedColumnOrder()
    {
        using var data = Dataset();
        var csv = await CsvExportService.PreviewAsync(data, new()
        {
            Signals = [A], Columns = ["signal_name", "channel", "value", "unit"]
        }, default);
        var rows = Parse(csv);
        Assert.Equal(["signal_name", "channel", "value", "unit"], rows[0]);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Position", "can1", "0.10000000000000002", "µm"], rows[1]);
        Assert.Equal(["Position", "can1", "23.75", "µm"], rows[2]);
    }

    [Theory]
    [InlineData(CsvTimeOrigin.Original, "10000001001")]
    [InlineData(CsvTimeOrigin.FirstLogRecord, "1001")]
    [InlineData(CsvTimeOrigin.FirstExportedSample, "0")]
    public async Task RelativeOriginDoesNotChangeAbsoluteTimestamps(CsvTimeOrigin origin, string expected)
    {
        using var data = Dataset();
        var rows = Parse(await CsvExportService.PreviewAsync(data, new()
        {
            Signals = [A], Columns = ["time_ns", "timestamp_utc", "unix_time_ns"], TimeOrigin = origin
        }, default));
        Assert.Equal(expected, rows[1][0]);
        Assert.Equal("2026-09-25T08:00:10.000001001Z", rows[1][1]);
        Assert.Equal(MeasurementTimestamp.ToUnixNanoseconds(Start, 10_000_001_001).ToString(), rows[1][2]);
    }

    [Fact]
    public async Task RelativeTimePreservesNanosecondsAndOneOriginAcrossSignals()
    {
        using var data = Dataset(9_007_199_254_740_993_000);
        var rows = Parse(await CsvExportService.PreviewAsync(data, new()
        {
            Columns = ["time_s", "time_ns", "channel"], TimeOrigin = CsvTimeOrigin.FirstExportedSample
        }, default));
        Assert.Equal(["0", "0", "can2"], rows[1]);
        Assert.Equal(["1E-06", "1000", "can1"], rows[2]);
        Assert.Equal("2000", rows[3][1]);
        Assert.Equal("3000", rows[4][1]);
    }

    [Theory]
    [InlineData(";", ",")]
    [InlineData(",", ".")]
    [InlineData(",", ",")]
    [InlineData("\t", ".")]
    public async Task FormattingRoundTripsDelimitersQuotesNewlinesAndPrecision(string delimiter, string decimalSeparator)
    {
        var identity = A with { SignalName = "Position;left,\"right\"\t\r\nline" };
        using var data = new DatasetBuilder().Build([], [new(123, -1, -1, identity, 0.10000000000000002, BigInteger.One, "µm")],
            [], new DecoderDiagnostics(0, 0, 1, 0, 0, ""));
        var options = new CsvExportOptions
        {
            Columns = ["signal_name", "value", "unit"], Delimiter = delimiter, DecimalSeparator = decimalSeparator
        };
        var csv = await CsvExportService.PreviewAsync(data, options, default);
        var rows = Parse(csv, delimiter);
        Assert.Equal(identity.SignalName, rows[1][0]);
        Assert.Equal("0" + decimalSeparator + "10000000000000002", rows[1][1]);
        Assert.Equal("µm", rows[1][2]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavedFileMatchesPreviewAndEncoding(bool bom)
    {
        using var data = Dataset();
        var path = Path.GetTempFileName();
        try
        {
            var options = new CsvExportOptions { IncludeUtf8Bom = bom, Delimiter = ";", DecimalSeparator = "," };
            await new CsvExportService().ExportDecodedSignalsAsync(path, data, options, default);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(bom, bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.Equal(await CsvExportService.PreviewAsync(data, options, default), await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelAndInvalidOptionsPreserveExistingDestination()
    {
        using var data = Dataset();
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "previous export");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CsvExportService()
                .ExportDecodedSignalsAsync(path, data, new CancellationToken(true)));
            await Assert.ThrowsAsync<ArgumentException>(() => new CsvExportService()
                .ExportDecodedSignalsAsync(path, data, new() { Signals = [] }, default));
            await Assert.ThrowsAsync<ArgumentException>(() => new CsvExportService()
                .ExportDecodedSignalsAsync(path, data, new() { Columns = [] }, default));
            await Assert.ThrowsAsync<ArgumentException>(() => new CsvExportService()
                .ExportDecodedSignalsAsync(path, data, new() { Columns = ["not_a_column"] }, default));
            Assert.Equal("previous export", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PreviewLimitsRowsButExportIncludesEverySelectedSample()
    {
        using var data = new DatasetBuilder().Build([], Enumerable.Range(0, 12)
            .Select(i => new DecodedSignalSample(i, -1, -1, A, i, new BigInteger(i))).ToArray(),
            [], new DecoderDiagnostics(0, 0, 12, 0, 0, ""));
        var path = Path.GetTempFileName();
        try
        {
            var options = new CsvExportOptions { Columns = ["value"] };
            Assert.Equal(6, Parse(await CsvExportService.PreviewAsync(data, options, default)).Count);
            await new CsvExportService().ExportDecodedSignalsAsync(path, data, options, default);
            Assert.Equal(13, Parse(await File.ReadAllTextAsync(path)).Count);
        }
        finally { File.Delete(path); }
    }

    private static List<string[]> Parse(string text, string delimiter = ",")
    {
        using var reader = new CsvReader(new StringReader(text), new CsvConfiguration(CultureInfo.InvariantCulture)
        { Delimiter = delimiter, HasHeaderRecord = false });
        var rows = new List<string[]>();
        while (reader.Read()) rows.Add(reader.Parser.Record!.ToArray());
        return rows;
    }
}
