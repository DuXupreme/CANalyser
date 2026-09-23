using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class AnalysisBundleExportTests
{
    [Fact]
    public void ExportKeepsNumericDataCoverageUtcAndSplitsMinuteIntegrals()
    {
        using var dataset = Dataset([59, 61, 63, 100], [10, 20, 30, 40]);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
            AnalysisBundleExporter.Export(path, dataset, new("machine", "logger", "battery", 5, true),
                JsonSerializer.SerializeToElement(new { Value = 1.25, Missing = (double?)null }), ["logger/session/log.MF4"]);
            using var zip = ZipFile.OpenRead(path);
            string Read(string name) { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
            var rows = Read("signal_statistics.csv").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var values = rows[1].Trim().Trim('"').Split("\",\"");
            Assert.Equal("4", values[8]);
            Assert.Equal("4", values[18]); // The 37-second gap never counts as coverage.
            Assert.Equal("60", values[19]);
            Assert.Equal("15", values[20]);
            Assert.Equal("2026-09-21T00:00:59.000000000Z", values[12]);
            var minutes = Read("signal_minutes.csv");
            Assert.Contains("\"0\",\"60\"", minutes);
            Assert.Contains("\"60\",\"120\"", minutes);
            Assert.Contains("\"1\",\"10\",\"10\"", minutes);
            Assert.Contains("\"3\",\"50\",\"16.666666666666668\"", minutes);
            Assert.Contains("1.25", Read("analysis_values.csv"));
            Assert.Contains("\"Null\",\"\"", Read("analysis_values.csv"));
            Assert.Equal(5, Read("samples.csv").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            using var manifest = JsonDocument.Parse(Read("manifest.json"));
            Assert.Equal("Partial", manifest.RootElement.GetProperty("Completeness").GetString());
            Assert.Equal("battery", manifest.RootElement.GetProperty("Options").GetProperty("BatteryId").GetString());
        }
        finally { CultureInfo.CurrentCulture = oldCulture; File.Delete(path); }
    }

    [Fact]
    public void DeclaredGapsAndNonFiniteValuesNeverBecomeMeasuredTime()
    {
        using var dataset = Dataset([0, 1, 2, 3, 4], [10, 20, double.NaN, 30, 40], [new(.5, .8)]);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try
        {
            AnalysisBundleExporter.Export(path, dataset, new("", "", ""), JsonSerializer.SerializeToElement(new { }), []);
            using var zip = ZipFile.OpenRead(path);
            Assert.Null(zip.GetEntry("samples.csv"));
            using var reader = new StreamReader(zip.GetEntry("signal_statistics.csv")!.Open());
            reader.ReadLine();
            var row = reader.ReadLine()!.Trim('"').Split("\",\"");
            Assert.Equal("1", row[18]); Assert.Equal("30", row[19]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailureOrCancellationPreservesExistingDestinationAndCleansTemporaryFiles(bool cancelled)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "result.zip");
        File.WriteAllText(path, "original");
        using var dataset = Dataset([1, 0], [1, 2]);
        try
        {
            Assert.ThrowsAny<Exception>(() => AnalysisBundleExporter.Export(path, dataset, new("", "", "", 5, true),
                JsonSerializer.SerializeToElement(new { }), [], new CancellationToken(cancelled)));
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    private static CanDataset Dataset(double[] times, double[] values, MeasurementGap[]? gaps = null)
    {
        var identity = new SignalIdentity("CAN1", CanFrameFormat.Classic, false, 1, "Temperature", "Cell");
        return new CanDataset
        {
            RawFrames = [], MessageSummaries = [], SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries>(),
            SignalSeriesByLabel = new Dictionary<string, SignalSeries>(), SignalLabels = [identity.DisplayLabel],
            DecodedSamples = times.Select((t, i) => new DecodedSignalSample((long)(t * 1e9), i, i, identity, values[i], 0, "°C")).ToArray(),
            Diagnostics = new(0, 0, 0, 0, 0, "test"), StartTimeUtc = DateTimeOffset.Parse("2026-09-21T00:00:00Z"),
            Completeness = DatasetCompleteness.Partial,
            ImportReport = new("test", 0, 0, 0, 0, []) { Gaps = gaps ?? [] }
        };
    }
}
