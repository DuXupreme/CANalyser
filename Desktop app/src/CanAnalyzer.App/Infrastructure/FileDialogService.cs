using CanAnalyzer.App.Services;
using Microsoft.Win32;
using System.IO;

namespace CanAnalyzer.App.Infrastructure;

/// <inheritdoc />
public sealed class FileDialogService : IFileDialogService
{
    public string? PickLogFile(string? initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open CAN-log, CANedge MF4 of dashboard-ZIP",
            Filter = "CAN logs (*.mf4;*.zip;*.trc;*.log;*.txt)|*.mf4;*.zip;*.trc;*.log;*.txt|CANedge logs (*.mf4;*.zip)|*.mf4;*.zip|All files (*.*)|*.*",
            Multiselect = false
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickActuatorCsvFiles(string? initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecteer één of meer Actuator Testbench logs om te vergelijken",
            Filter = "Actuator Testbench CSV (*.csv)|*.csv|All files (*.*)|*.*",
            Multiselect = true
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
    }

    public string? PickDbcFile(string? initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open DBC file",
            Filter = "DBC files (*.dbc)|*.dbc|All files (*.*)|*.*",
            Multiselect = false
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveDbcFile(string? initialPath)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Database opslaan als DBC",
            Filter = "DBC files (*.dbc)|*.dbc|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".dbc",
            FileName = "can_database.dbc"
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickPresetFile(string? initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import plot layout preset",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = false
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SavePresetFile(string? initialPath)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export plot layout preset",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".json",
            FileName = "can_viewer_layout.json"
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveCsvFile(string? initialPath)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export decoded signals to CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".csv",
            FileName = "decoded_can_signals.csv"
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static void ApplyInitialPath(FileDialog dialog, string? initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath))
        {
            return;
        }

        try
        {
            var directory = Directory.Exists(initialPath)
                ? initialPath
                : Path.GetDirectoryName(initialPath);

            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }
        }
        catch
        {
            // Ignore invalid path hints.
        }
    }
}
