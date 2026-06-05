using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.Models;

/// <summary>
/// Selectable signal entry used by Analysis signal list.
/// </summary>
public sealed partial class SignalSelectionItem : ObservableObject
{
    public SignalSelectionItem(string label)
    {
        Label = label;
    }

    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;
}
