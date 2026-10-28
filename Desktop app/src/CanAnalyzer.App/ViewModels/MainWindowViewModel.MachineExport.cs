using System.IO;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Microsoft.Extensions.Logging;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void InitializeAnalysisProfiles()
    {
        JoystickAnalytics.ConfigureProfiles(LoadedSettings.AnalysisProfiles);
        JoystickAnalytics.ProfileConfirmed += async profile =>
        {
            try
            {
                LoadedSettings.AnalysisProfiles.RemoveAll(existing => existing.Id == profile.Id);
                LoadedSettings.AnalysisProfiles.Add(profile);
                await SaveSettingsAsync(CancellationToken.None);
                StatusText = "Analyseprofiel opgeslagen voor volgende passende machineopnamen.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not persist confirmed analysis profile.");
                _messageDialogService.ShowError("Profiel niet opgeslagen", ex.Message);
            }
        };
    }

    private async Task ExportAnalysesAsync()
    {
        if (_dataset is null || IsAnyBusy || HasPendingSourceSelection) return;
        var dataset = _dataset;
        var directory = LoadedSettings.MachineExportDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            var folder = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Kies de vaste map voor machine-analyses", Multiselect = false
            };
            if (folder.ShowDialog() != true) return;
            directory = folder.FolderName;
        }
        // This is a frozen, full-dataset run; current tab/plot selections are never inputs.
        var profile = AnalysisProfileResolver.Resolve(dataset, LoadedSettings.AnalysisProfiles);
        var request = new AnalysisRunRequest { Profile = profile, IncludeSamples = LoadedSettings.MachineExportIncludeSamples };
        var filename = $"canalyser-machine-analyse-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.zip";
        var output = Path.Combine(directory, filename);
        IsBusy = true;
        ProgressLabel = "Volledige machine-analyse en minuuttrends exporteren…";
        ProgressValue = 0;
        _loadCts = new CancellationTokenSource();
        try
        {
            var token = _loadCts.Token;
            using var lease = dataset.AcquireReadLease();
            await Task.Run(() => AnalysisBundleExporter.ExportStandard(output, dataset, request, token), token);
            LoadedSettings.MachineExportDirectory = directory;
            SettingsDiagnostics.MachineExportDirectory = directory;
            try { await SaveSettingsAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Export succeeded, but its folder could not be remembered."); }
            ProgressValue = 100;
            StatusText = $"Machine-analyse opgeslagen: {output}";
            _messageDialogService.ShowInfo("Machine-analyse opgeslagen", output +
                "\n\nVolledige opname, vaste analyses en minuuttrends. Ontbrekende gegevens zijn expliciet gemarkeerd.");
        }
        catch (OperationCanceledException) { StatusText = "Machine-analyse exporteren geannuleerd."; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Standard machine export failed.");
            StatusText = "Machine-analyse kon niet worden opgeslagen.";
            _messageDialogService.ShowError("Machine-analyse mislukt", ex.Message);
        }
        finally
        {
            _loadCts?.Dispose(); _loadCts = null;
            IsBusy = false; ProgressLabel = "Klaar.";
        }
    }
}
