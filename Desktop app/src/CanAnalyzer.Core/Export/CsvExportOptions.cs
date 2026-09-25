using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Export;

public enum CsvTimeOrigin { Original, FirstLogRecord, FirstExportedSample }

public sealed record CsvExportColumn(string Name, string Description);

/// <summary>CSV rows retain individual sample times; values are never resampled or rounded.</summary>
public sealed record CsvExportOptions
{
    public static IReadOnlyList<CsvExportColumn> AvailableColumns { get; } = Array.AsReadOnly(new[]
    {
        new CsvExportColumn("time_s", "Tijd in seconden (gekozen nulpunt)"),
        new("timestamp_utc", "Absolute datum en tijd (ISO 8601, UTC)"),
        new("unix_time_ns", "Absolute Unix-tijd in nanoseconden"),
        new("frame_id", "CAN-ID (decimaal)"), new("message_name", "Berichtnaam"),
        new("signal_name", "Signaalnaam"), new("value", "Gedecodeerde waarde"),
        new("label", "Volledige signaalnaam inclusief kanaal en CAN-ID"),
        new("time_ns", "Tijd in nanoseconden (gekozen nulpunt)"),
        new("frame_index", "Frame-index"), new("source_line", "Bronregel"),
        new("channel", "CAN-kanaal"), new("frame_format", "CAN / CAN FD"),
        new("is_extended", "Extended CAN-ID"), new("dlc_code", "DLC-code"),
        new("payload_length", "Payloadlengte"), new("raw_value", "Ruwe signaalwaarde"),
        new("unit", "Eenheid"), new("decode_quality", "Decodeerkwaliteit"),
        new("dataset_status", "COMPLETE / PARTIAL"), new("source_log_sha256", "SHA-256 bronlog"),
        new("dbc_sha256", "SHA-256 DBC"), new("app_version", "CANalyser-versie"),
        new("import_mode", "Importmodus")
    });

    public static IReadOnlyList<string> CompactColumns { get; } = Array.AsReadOnly(new[]
    {
        "time_s", "frame_id", "message_name", "signal_name", "value", "channel", "unit", "dataset_status"
    });

    /// <summary>Null means all signals; an empty selection is rejected.</summary>
    public IReadOnlyCollection<SignalIdentity>? Signals { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = AvailableColumns.Select(c => c.Name).ToArray();
    public CsvTimeOrigin TimeOrigin { get; init; }
    public string Delimiter { get; init; } = ",";
    public string DecimalSeparator { get; init; } = ".";
    public bool IncludeUtf8Bom { get; init; }
}
