namespace CanAnalyzer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // Called once by the shell constructor after its child viewmodels have been assigned.
    private void InitializeAnalysisStorage()
    {
        SettingsDiagnostics.BindAnalysisStorageHandlers(CloseDatasetForStorageAsync,
            () => !_disposed && _dataset is not null && !IsBusy);
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsAnyBusy) or nameof(IsBusy))
                SettingsDiagnostics.NotifyStorageStateChanged();
        };
    }

    private async Task CloseDatasetForStorageAsync()
    {
        if (_disposed || IsBusy)
            throw new InvalidOperationException("Wacht tot de huidige import of berekening klaar is voordat je de dataset sluit.");
        IsBusy = true; // Block a replacement import while readers are being drained.
        try
        {
            ProgressLabel = "Analyses stoppen en dataset sluiten...";
            await Analysis.CancelAndWaitAsync();
            await JoystickAnalytics.CancelAndWaitAsync();
            await Analysis.ActiveUsage.CancelAndWaitAsync();
            await Busmaster.CancelAndWaitAsync();
            Analysis.ClearDataset();
            Analysis.ActiveUsage.ClearDataset();
            JoystickAnalytics.ClearDataset();
            ReleaseCurrentDataset();
            StatusText = "Dataset gesloten. Bronbestanden en opgeslagen profielen blijven behouden.";
            ProgressLabel = "Dataset gesloten.";
            ProgressValue = 0;
        }
        finally
        {
            IsBusy = false;
            SettingsDiagnostics.NotifyStorageStateChanged();
        }
    }
}
