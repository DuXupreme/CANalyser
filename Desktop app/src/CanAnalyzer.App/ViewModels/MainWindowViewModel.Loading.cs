using System.IO;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _loadingIndicatorDelay;
    private bool _datasetLoadActive;
    private bool _showDatasetLoading;

    public bool ShowDatasetLoading => _showDatasetLoading && !IsRepairWizardOpen;
    public string LoadingSourceName => Path.GetFileName(LogFilePath ?? string.Empty);

    private void BeginDatasetLoading()
    {
        EndDatasetLoading();
        _datasetLoadActive = true;
        OnPropertyChanged(nameof(LoadingSourceName));
        _loadingIndicatorDelay = new CancellationTokenSource();
        _ = RevealDatasetLoadingAsync(_loadingIndicatorDelay.Token);
    }

    private async Task RevealDatasetLoadingAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
            if (token.IsCancellationRequested || _disposed || !_datasetLoadActive) return;
            _showDatasetLoading = true;
            OnPropertyChanged(nameof(ShowDatasetLoading));
        }
        catch (OperationCanceledException) { }
    }

    private void EndDatasetLoading()
    {
        _datasetLoadActive = false;
        _loadingIndicatorDelay?.Cancel();
        _loadingIndicatorDelay?.Dispose();
        _loadingIndicatorDelay = null;
        _showDatasetLoading = false;
        OnPropertyChanged(nameof(ShowDatasetLoading));
    }
}
