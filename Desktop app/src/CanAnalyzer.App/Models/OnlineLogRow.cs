using CommunityToolkit.Mvvm.ComponentModel;
using CanAnalyzer.App.Services;

namespace CanAnalyzer.App.Models;

public sealed partial class OnlineLogRow : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Machine { get; init; }
    public required string Logger { get; init; }
    public required string Session { get; init; }
    public required DateTimeOffset? RecordedAt { get; init; }
    public required DateTimeOffset UploadedAt { get; init; }
    public required long SizeBytes { get; init; }
    public OnlineLogFile? Provenance { get; init; }
    public bool IsExcluded => Provenance?.Annotation?.Excluded == true;
    public string AnnotationDisplay => Provenance?.Annotation is not { } annotation ? "Markering onbekend"
        : annotation.Excluded ? "Niet nuttig: " + annotation.Reason : "Niet gemarkeerd";
    public string MachineDisplay => Provenance?.MachineAssignmentQuality == "verified-installation" ? Machine
        : string.Equals(Logger, "48EDFD35", StringComparison.OrdinalIgnoreCase) ? "Onbekend (machine 1/3)"
        : Machine + " (onbevestigd)";
    public string ClockDisplay => Provenance?.ClockQuality switch
    {
        "verified" => "Bevestigd met tijdbron",
        "suspect" => "Loggerklok verdacht",
        _ => "Loggerklok onbevestigd"
    };
    public string RecordedDisplay => RecordedAt?.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss zzz") ?? "Loggerdatum onbekend";
    public string UploadedDisplay => UploadedAt == default ? "Uploadtijd onbekend" : UploadedAt.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss zzz");
    public string SizeDisplay => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024d / 1024d:N1} MB"
        : $"{SizeBytes / 1024d:N0} kB";
}
