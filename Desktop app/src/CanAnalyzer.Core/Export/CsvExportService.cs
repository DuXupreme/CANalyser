using System.Globalization;
using System.Text;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;
using CsvHelper;
using CsvHelper.Configuration;

namespace CanAnalyzer.Core.Export;

/// <summary>Streaming decoded CSV export with exact values and selectable provenance.</summary>
public sealed class CsvExportService : ICsvExportService
{
    public Task ExportDecodedSignalsAsync(string filePath, CanDataset dataset, CancellationToken cancellationToken) =>
        ExportDecodedSignalsAsync(filePath, dataset, new CsvExportOptions(), cancellationToken);

    public async Task ExportDecodedSignalsAsync(string filePath, CanDataset dataset, CsvExportOptions options,
        CancellationToken cancellationToken)
    {
        using var reader = dataset.AcquireReadLease();
        // Failed/cancelled exports must not replace existing files or leave plausible partial CSVs.
        var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(options.IncludeUtf8Bom)))
                await WriteAsync(writer, dataset, options, int.MaxValue, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static async Task<string> PreviewAsync(CanDataset dataset, CsvExportOptions options,
        CancellationToken cancellationToken)
    {
        using var reader = dataset.AcquireReadLease();
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        await WriteAsync(writer, dataset, options, 5, cancellationToken).ConfigureAwait(false);
        return writer.ToString();
    }

    private static async Task WriteAsync(TextWriter writer, CanDataset dataset, CsvExportOptions options,
        int maxRows, CancellationToken token)
    {
        var columns = options.Columns.ToArray();
        var signals = options.Signals?.ToHashSet();
        if (columns.Length == 0 || columns.Distinct(StringComparer.Ordinal).Count() != columns.Length ||
            columns.Any(name => !CsvExportOptions.AvailableColumns.Any(c => c.Name == name)))
            throw new ArgumentException("Kies minstens één geldige kolom, zonder duplicaten.", nameof(options));
        if (signals is { Count: 0 }) throw new ArgumentException("Kies minstens één signaal.", nameof(options));
        if (!Enum.IsDefined(options.TimeOrigin) || options.Delimiter is not ("," or ";" or "\t") ||
            options.DecimalSeparator is not ("." or ","))
            throw new ArgumentException("Ongeldige CSV-opmaak of tijdkeuze.", nameof(options));

        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = options.DecimalSeparator;
        await using var csv = new CsvWriter(writer, new CsvConfiguration(culture)
        {
            Delimiter = options.Delimiter, NewLine = "\r\n"
        }, leaveOpen: true);
        foreach (var column in columns) csv.WriteField(column);
        await csv.NextRecordAsync().ConfigureAwait(false);

        long? origin = options.TimeOrigin switch
        {
            CsvTimeOrigin.Original => 0,
            CsvTimeOrigin.FirstLogRecord => dataset.RawFrames.Count > 0
                ? dataset.FirstRecordOffsetNanoseconds
                : dataset.DecodedSamples.Count > 0 ? dataset.DecodedSamples[0].TimestampNanoseconds : 0,
            _ => null
        };
        var needsFrame = columns.Contains("dlc_code") || columns.Contains("payload_length");
        var rows = 0;
        foreach (var sample in dataset.DecodedSamples)
        {
            token.ThrowIfCancellationRequested();
            if (signals is not null && !signals.Contains(sample.Identity)) continue;
            origin ??= sample.TimestampNanoseconds;
            // Subtract before converting to seconds to preserve small differences at large offsets.
            var timeNs = checked(sample.TimestampNanoseconds - origin.Value);
            var frame = needsFrame ? ResolveFrame(dataset.RawFrames, sample.FrameIndex) : null;
            foreach (var column in columns)
            {
                object? value = column switch
                {
                    "time_s" => (timeNs / 1_000_000_000d).ToString("R", culture),
                    "time_ns" => timeNs,
                    "timestamp_utc" => dataset.StartTimeUtc is { } start
                        ? MeasurementTimestamp.FormatUtcIso8601(start, sample.TimestampNanoseconds) : "",
                    "unix_time_ns" => dataset.StartTimeUtc is { } utc
                        ? MeasurementTimestamp.ToUnixNanoseconds(utc, sample.TimestampNanoseconds).ToString(CultureInfo.InvariantCulture) : "",
                    "frame_id" => sample.FrameId, "message_name" => sample.MessageName,
                    "signal_name" => sample.SignalName, "value" => sample.Value.ToString("R", culture),
                    "label" => sample.Label, "frame_index" => sample.FrameIndex,
                    "source_line" => sample.SourceLineNumber, "channel" => sample.Channel,
                    "frame_format" => sample.Identity.FrameFormat.ToString(), "is_extended" => sample.Identity.IsExtended,
                    "dlc_code" => frame?.Dlc ?? 0, "payload_length" => frame?.PayloadLength ?? 0,
                    "raw_value" => sample.RawValue.ToString(CultureInfo.InvariantCulture), "unit" => sample.Unit,
                    "decode_quality" => sample.Quality.ToString(),
                    "dataset_status" => dataset.Completeness.ToString().ToUpperInvariant(),
                    "source_log_sha256" => dataset.SourceLogSha256, "dbc_sha256" => dataset.DbcSha256,
                    "app_version" => dataset.ApplicationVersion, "import_mode" => dataset.ImportReport?.Mode.ToString() ?? "",
                    _ => throw new InvalidOperationException("Onbekende CSV-kolom.")
                };
                csv.WriteField(value);
            }
            await csv.NextRecordAsync().ConfigureAwait(false);
            if (++rows >= maxRows) break;
        }
        token.ThrowIfCancellationRequested();
    }

    private static RawCanFrame? ResolveFrame(IReadOnlyList<RawCanFrame> frames, long frameIndex)
    {
        if (frameIndex >= 0 && frameIndex < frames.Count && frames[(int)frameIndex].FrameIndex == frameIndex)
            return frames[(int)frameIndex];
        return frames.FirstOrDefault(frame => frame.FrameIndex == frameIndex);
    }
}
