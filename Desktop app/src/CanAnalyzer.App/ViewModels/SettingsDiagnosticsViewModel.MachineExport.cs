using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class SettingsDiagnosticsViewModel
{
    [ObservableProperty] private string _machineExportDirectory = string.Empty;
    [ObservableProperty] private bool _machineExportIncludeSamples;

    [RelayCommand]
    private void ChooseMachineExportDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Vaste map voor machine-analyses", Multiselect = false };
        if (dialog.ShowDialog() == true) MachineExportDirectory = dialog.FolderName;
    }
}
