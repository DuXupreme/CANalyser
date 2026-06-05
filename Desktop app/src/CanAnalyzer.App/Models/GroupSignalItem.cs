using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.Models;

/// <summary>
/// Signal entry with per-line offset inside a plot group.
/// </summary>
public sealed partial class GroupSignalItem : ObservableObject
{
    public GroupSignalItem(string label, double offset = 0)
    {
        Label = label;
        _offset = offset;
    }

    public string Label { get; }

    [ObservableProperty]
    private double _offset;
}
