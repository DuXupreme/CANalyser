using System.Globalization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CsvHelper;

namespace CanAnalyzer.Core.Export;

/// <inheritdoc />
public sealed class CsvExportService : ICsvExportService
{
    public async Task ExportDecodedSignalsAsync(
        string filePath,
        IEnumerable<DecodedSignalSample> samples,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        csv.WriteField("time_s");
        csv.WriteField("frame_id");
        csv.WriteField("message_name");
        csv.WriteField("signal_name");
        csv.WriteField("value");
        csv.WriteField("label");
        await csv.NextRecordAsync();

        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            csv.WriteField(sample.TimeSeconds);
            csv.WriteField(sample.FrameId);
            csv.WriteField(sample.MessageName);
            csv.WriteField(sample.SignalName);
            csv.WriteField(sample.Value);
            csv.WriteField(sample.Label);
            await csv.NextRecordAsync();
        }
    }
}
