using CommunityToolkit.Mvvm.ComponentModel;

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
    public required DateTimeOffset CreatedAt { get; init; }
    public required long SizeBytes { get; init; }
    public string CreatedDisplay => CreatedAt.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss");
    public string SizeDisplay => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024d / 1024d:N1} MB"
        : $"{SizeBytes / 1024d:N0} kB";
}
