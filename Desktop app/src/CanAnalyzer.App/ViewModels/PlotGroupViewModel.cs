using System.Collections.ObjectModel;
using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.ViewModels;

/// <summary>
/// Editable plot group view model.
/// </summary>
public sealed partial class PlotGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _lockYAxis;

    public ObservableCollection<GroupSignalItem> Signals { get; } = [];

    public void AddSignals(IEnumerable<string> labels)
    {
        var existing = Signals.Select(item => item.Label).ToHashSet(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (existing.Add(label))
            {
                Signals.Add(new GroupSignalItem(label));
            }
        }
    }

    public void RemoveSignal(GroupSignalItem? item)
    {
        if (item is null)
        {
            return;
        }

        Signals.Remove(item);
    }

    public PlotGroup ToDomainModel()
    {
        return new PlotGroup
        {
            Title = Title,
            LockYAxis = LockYAxis,
            Signals = Signals.Select(item => item.Label).ToList(),
            Offsets = Signals.ToDictionary(item => item.Label, item => item.Offset, StringComparer.Ordinal)
        };
    }

    public static PlotGroupViewModel FromDomainModel(PlotGroup group)
    {
        var vm = new PlotGroupViewModel
        {
            Title = group.Title,
            LockYAxis = group.LockYAxis
        };

        foreach (var label in group.Signals)
        {
            group.Offsets.TryGetValue(label, out var offset);
            vm.Signals.Add(new GroupSignalItem(label, offset));
        }

        return vm;
    }
}
