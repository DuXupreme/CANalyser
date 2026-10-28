using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string? _loadedSelectionLogPath;
    private string? _loadedSelectionDbcPath;
    private bool _loadedSelectionIsActuatorCsv;

    public string ApplicationVersion { get; } = typeof(MainWindowViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "onbekend";

    public string WindowTitle => $"CANalyser {ApplicationVersion}" +
        (_dataset is null ? string.Empty : $" — {LoadedSourceName}");

    private string LoadedSourceName => _loadedSelectionIsActuatorCsv && _dataset?.SourceFiles.FirstOrDefault() is { } csv
        ? csv.Name + (_dataset.SourceFiles.Count > 1 ? $" ({_dataset.SourceFiles.Count} CSV-runs)" : string.Empty)
        : Path.GetFileName(string.IsNullOrWhiteSpace(_dataset?.SourceLogPath)
            ? _loadedSelectionLogPath : _dataset?.SourceLogPath) is { Length: > 0 } name ? name : "geladen opname";

    public bool HasPendingSourceSelection => _dataset is not null &&
        (!string.Equals(LogFilePath, _loadedSelectionLogPath, StringComparison.OrdinalIgnoreCase) ||
         !string.Equals(DbcFilePath, _loadedSelectionDbcPath, StringComparison.OrdinalIgnoreCase));

    private string? LoadedDbcPath => _loadedSelectionIsActuatorCsv ? null : string.IsNullOrWhiteSpace(_dataset?.SourceDbcPath)
        ? _loadedSelectionDbcPath : _dataset?.SourceDbcPath;

    public string LoadedSourceSummary => _dataset is null ? "Nog geen opname geladen" :
        $"Geopend: {LoadedSourceName}" +
        (_loadedSelectionIsActuatorCsv ? " · Actuator CSV (geen DBC)" :
            string.IsNullOrWhiteSpace(LoadedDbcPath) ? string.Empty : $" · DBC: {Path.GetFileName(LoadedDbcPath)}");

    public bool ShowSourceSelectionNotice => HasPendingSourceSelection ||
        _dataset is null && (!string.IsNullOrWhiteSpace(LogFilePath) || !string.IsNullOrWhiteSpace(DbcFilePath));

    public string SourceSelectionNotice => HasPendingSourceSelection
        ? $"Nieuwe selectie nog niet geladen: {SelectedSourceNames}. Je bekijkt nog {LoadedSourceName}. Laad de nieuwe selectie of behoud de huidige opname om te exporteren."
        : _loadedSelectionIsActuatorCsv && string.Equals(LogFilePath, _loadedSelectionLogPath, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(DbcFilePath)
            ? $"Gekozen: {Path.GetFileName(LogFilePath)}. Gebruik Actuator CSV om deze CSV-runs opnieuw te openen; een DBC is niet nodig."
            : $"Gekozen: {SelectedSourceNames}. Klik op Laden/Decoderen om deze opname te openen.";

    private string SelectedSourceNames =>
        $"log: {(Path.GetFileName(LogFilePath) is { Length: > 0 } log ? log : "nog kiezen")}; " +
        $"DBC: {(Path.GetFileName(DbcFilePath) is { Length: > 0 } dbc ? dbc : "nog kiezen")}";

    public IRelayCommand KeepLoadedSourceSelectionCommand { get; private set; } = null!;

    private void MarkSourceSelectionLoaded(bool isActuatorCsv = false)
    {
        _loadedSelectionIsActuatorCsv = isActuatorCsv;
        _loadedSelectionLogPath = LogFilePath;
        _loadedSelectionDbcPath = DbcFilePath;
        RefreshSourceSelectionState();
    }

    private void KeepLoadedSourceSelection()
    {
        if (_dataset is null || IsBusy) return;
        LogFilePath = _loadedSelectionLogPath;
        DbcFilePath = _loadedSelectionDbcPath;
    }

    private void RefreshSourceSelectionState()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(LoadedSourceSummary));
        OnPropertyChanged(nameof(HasPendingSourceSelection));
        OnPropertyChanged(nameof(ShowSourceSelectionNotice));
        OnPropertyChanged(nameof(SourceSelectionNotice));
        KeepLoadedSourceSelectionCommand?.NotifyCanExecuteChanged();
    }
}
