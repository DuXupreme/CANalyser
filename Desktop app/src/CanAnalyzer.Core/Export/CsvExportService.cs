using System.Globalization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CsvHelper;

namespace CanAnalyzer.Core.Export;

/// <summary>Round-trip, provenance-complete decoded CSV export.</summary>
public sealed class CsvExportService : ICsvExportService
{
    public async Task ExportDecodedSignalsAsync(string filePath, CanDataset dataset, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        var headers = new[]
        {
            "time_s", "frame_id", "message_name", "signal_name", "value", "label",
            "time_ns", "frame_index", "source_line", "channel", "frame_format", "is_extended",
            "dlc_code", "payload_length", "raw_value", "unit", "decode_quality", "dataset_status",
            "source_log_sha256", "dbc_sha256", "app_version", "import_mode"
        };
        foreach (var header in headers) csv.WriteField(header);
        await csv.NextRecordAsync().ConfigureAwait(false);

        foreach (var sample in dataset.DecodedSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = ResolveFrame(dataset.RawFrames, sample.FrameIndex);
            csv.WriteField(sample.TimeSeconds.ToString("R", CultureInfo.InvariantCulture));
            csv.WriteField(sample.FrameId);
            csv.WriteField(sample.MessageName);
            csv.WriteField(sample.SignalName);
            csv.WriteField(sample.Value.ToString("R", CultureInfo.InvariantCulture));
            csv.WriteField(sample.Label);
            csv.WriteField(sample.TimestampNanoseconds);
            csv.WriteField(sample.FrameIndex);
            csv.WriteField(sample.SourceLineNumber);
            csv.WriteField(sample.Channel);
            csv.WriteField(sample.Identity.FrameFormat.ToString());
            csv.WriteField(sample.Identity.IsExtended);
            csv.WriteField(frame?.Dlc ?? 0);
            csv.WriteField(frame?.PayloadLength ?? 0);
            csv.WriteField(sample.RawValue.ToString(CultureInfo.InvariantCulture));
            csv.WriteField(sample.Unit);
            csv.WriteField(sample.Quality.ToString());
            csv.WriteField(dataset.Completeness.ToString().ToUpperInvariant());
            csv.WriteField(dataset.SourceLogSha256);
            csv.WriteField(dataset.DbcSha256);
            csv.WriteField(dataset.ApplicationVersion);
            csv.WriteField(dataset.ImportReport?.Mode.ToString() ?? string.Empty);
            await csv.NextRecordAsync().ConfigureAwait(false);
        }
    }

    private static RawCanFrame? ResolveFrame(IReadOnlyList<RawCanFrame> frames, long frameIndex)
    {
        if (frameIndex >= 0 && frameIndex < frames.Count && frames[(int)frameIndex].FrameIndex == frameIndex)
            return frames[(int)frameIndex];
        return frames.FirstOrDefault(frame => frame.FrameIndex == frameIndex);
    }
}
