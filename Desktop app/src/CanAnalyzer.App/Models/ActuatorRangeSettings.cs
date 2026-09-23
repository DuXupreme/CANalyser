using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.Models;

public sealed partial class ActuatorRangeSettings(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private string? _centerSignal;
    [ObservableProperty] private double? _manualCenter;
    [ObservableProperty] private double? _halfRange;
    [ObservableProperty] private string? _halfRangeSignal;
    [ObservableProperty] private string? _setpointSignal;
    [ObservableProperty] private string? _runSignal;
    [ObservableProperty] private double _runValue = 1;
}
