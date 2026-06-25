using System.Diagnostics;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.IO;

namespace CanAnalyzer.App.ViewModels;

/// <summary>
/// Main shell view model: file operations, load/decode, export, global status/progress.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ICanAnalysisPipeline _analysisPipeline;
    private readonly ICsvExportService _csvExportService;
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageDialogService _messageDialogService;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ILogger<MainWindowViewModel> _logger;
    private CancellationTokenSource? _loadCts;
    private CanDataset? _dataset;

    [ObservableProperty]
    private string? _logFilePath;

    [ObservableProperty]
    private string? _dbcFilePath;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private string _progressLabel = "Klaar.";

    [ObservableProperty]
    private string _statusText = "Selecteer log + DBC en klik Laden/Decoderen.";

    public MainWindowViewModel(
        ICanAnalysisPipeline analysisPipeline,
        ICsvExportService csvExportService,
        IFileDialogService fileDialogService,
        IMessageDialogService messageDialogService,
        IAppSettingsStore settingsStore,
        AnalysisViewModel analysis,
        JoystickAnalyticsViewModel joystickAnalytics,
        RawFramesViewModel rawFrames,
        BusmasterViewModel busmaster,
        SettingsDiagnosticsViewModel settingsDiagnostics,
        DbcEditorViewModel dbcEditor,
        ILogger<MainWindowViewModel> logger)
    {
        _analysisPipeline = analysisPipeline;
        _csvExportService = csvExportService;
        _fileDialogService = fileDialogService;
        _messageDialogService = messageDialogService;
        _settingsStore = settingsStore;
        _logger = logger;

        Analysis = analysis;
        JoystickAnalytics = joystickAnalytics;
        RawFrames = rawFrames;
        Busmaster = busmaster;
        SettingsDiagnostics = settingsDiagnostics;
        DbcEditor = dbcEditor;

        LoadedSettings = _settingsStore.Load();
        LogFilePath = LoadedSettings.LastLogFilePath;
        DbcFilePath = LoadedSettings.LastDbcFilePath;
        Analysis.ApplyViewOptions(LoadedSettings.LastPlotViewOptions);
        RawFrames.ApplyFilterOptions(LoadedSettings.LastRawFrameFilter);
        SettingsDiagnostics.ApplySettings(LoadedSettings, _settingsStore.SettingsPath);
        SettingsDiagnostics.BindApplySettingsHandler(ApplyProgramSettingsFromUiAsync);

        BrowseLogFileCommand = new RelayCommand(BrowseLogFile);
        BrowseDbcFileCommand = new RelayCommand(BrowseDbcFile);
        LoadAndDecodeCommand = new AsyncRelayCommand(LoadAndDecodeAsync, CanLoadAndDecode);
        CancelCommand = new RelayCommand(CancelLoad, () => IsBusy);
        ExportDecodedCsvCommand = new AsyncRelayCommand(ExportDecodedCsvAsync, CanExportDecodedCsv);
        ExportLayoutCommand = new AsyncRelayCommand(ExportLayoutAsync);
        ImportLayoutCommand = new AsyncRelayCommand(ImportLayoutAsync);
        UpdateCommandStates();
    }

    public AppSettings LoadedSettings { get; }

    public AnalysisViewModel Analysis { get; }

    public JoystickAnalyticsViewModel JoystickAnalytics { get; }

    public RawFramesViewModel RawFrames { get; }

    public BusmasterViewModel Busmaster { get; }

    public SettingsDiagnosticsViewModel SettingsDiagnostics { get; }

    public DbcEditorViewModel DbcEditor { get; }

    public IRelayCommand BrowseLogFileCommand { get; }

    public IRelayCommand BrowseDbcFileCommand { get; }

    public IAsyncRelayCommand LoadAndDecodeCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand ExportDecodedCsvCommand { get; }

    public IAsyncRelayCommand ExportLayoutCommand { get; }

    public IAsyncRelayCommand ImportLayoutCommand { get; }

    partial void OnLogFilePathChanged(string? value)
    {
        LoadedSettings.LastLogFilePath = value;
        SettingsDiagnostics.LogFilePath = value;
        UpdateCommandStates();
    }

    partial void OnDbcFilePathChanged(string? value)
    {
        LoadedSettings.LastDbcFilePath = value;
        SettingsDiagnostics.DbcFilePath = value;
        UpdateCommandStates();
    }

    public async Task PersistWindowStateAsync(
        double width,
        double height,
        double left,
        double top,
        bool maximized,
        CancellationToken cancellationToken)
    {
        LoadedSettings.WindowWidth = width;
        LoadedSettings.WindowHeight = height;
        LoadedSettings.WindowLeft = left;
        LoadedSettings.WindowTop = top;
        LoadedSettings.WindowMaximized = maximized;
        await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool CanLoadAndDecode()
    {
        return !IsBusy &&
               !string.IsNullOrWhiteSpace(LogFilePath) &&
               !string.IsNullOrWhiteSpace(DbcFilePath);
    }

    private bool CanExportDecodedCsv()
    {
        return !IsBusy && _dataset is not null;
    }

    private void BrowseLogFile()
    {
        var selected = _fileDialogService.PickLogFile(LogFilePath);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            LogFilePath = selected;
        }
    }

    private void BrowseDbcFile()
    {
        var selected = _fileDialogService.PickDbcFile(DbcFilePath);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            DbcFilePath = selected;
        }
    }

    private async Task LoadAndDecodeAsync()
    {
        if (string.IsNullOrWhiteSpace(LogFilePath) || string.IsNullOrWhiteSpace(DbcFilePath))
        {
            _messageDialogService.ShowInfo("Bestanden ontbreken", "Kies eerst zowel een CAN-logbestand als een DBC-bestand.");
            return;
        }

        if (!File.Exists(LogFilePath))
        {
            _messageDialogService.ShowError("Logbestand ontbreekt", $"Bestand niet gevonden:\n{LogFilePath}");
            return;
        }

        if (!File.Exists(DbcFilePath))
        {
            _messageDialogService.ShowError("DBC-bestand ontbreekt", $"Bestand niet gevonden:\n{DbcFilePath}");
            return;
        }

        IsBusy = true;
        _dataset?.Dispose();
        _dataset = null;
        ProgressValue = 0;
        ProgressLabel = "Start verwerking...";
        SettingsDiagnostics.LastErrorDetails = string.Empty;
        _loadCts = new CancellationTokenSource();
        UpdateCommandStates();

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<LoadProgress>(item =>
            {
                ProgressLabel = item.Label;
                ProgressValue = Math.Clamp(item.Percent, 0, 100);
            });

            try
            {
                _dataset = await _analysisPipeline.LoadAsync(LogFilePath, DbcFilePath, ImportMode.Strict, progress, _loadCts.Token);
            }
            catch (ImportIntegrityException integrityException)
            {
                var reportText = FormatImportReport(integrityException.Report);
                SettingsDiagnostics.LastErrorDetails = reportText;
                var proceed = _messageDialogService.Confirm(
                    "Integriteitsfouten gevonden",
                    $"{integrityException.Message}\n\n{reportText}\n\n" +
                    "Wil je bewust doorgaan in PARTIAL-modus? Afgewezen regels en frames worden nooit met kunstmatige waarden aangevuld.");
                if (!proceed)
                {
                    StatusText = "Import geblokkeerd wegens integriteitsfouten.";
                    ProgressLabel = "Strikte import afgebroken.";
                    return;
                }

                _dataset = await _analysisPipeline.LoadAsync(LogFilePath, DbcFilePath, ImportMode.Partial, progress, _loadCts.Token);
            }

            var channels = _dataset.RawFrames
                .Select(static frame => string.IsNullOrWhiteSpace(frame.Channel) ? "(onbekend)" : frame.Channel)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static channel => channel, StringComparer.Ordinal)
                .ToArray();
            if (channels.Length > 1)
            {
                var dbcName = Path.GetFileName(DbcFilePath);
                var mapping = string.Join(Environment.NewLine, channels.Select(channel => $"• {channel} → {dbcName}"));
                var confirmed = _messageDialogService.Confirm(
                    "DBC-toewijzing per kanaal bevestigen",
                    $"De log bevat {channels.Length} kanalen. Bevestig expliciet dat dezelfde DBC op ieder kanaal van toepassing is:\n\n{mapping}\n\n" +
                    "Kies Annuleren als een kanaal een andere DBC vereist; de analyse wordt dan niet geopend.");
                if (!confirmed)
                {
                    _dataset.Dispose();
                    _dataset = null;
                    StatusText = "Analyse geblokkeerd: DBC-toewijzing per kanaal niet bevestigd.";
                    ProgressLabel = "DBC-toewijzing afgebroken.";
                    return;
                }
            }

            Analysis.LoadDataset(_dataset);
            JoystickAnalytics.LoadDataset(_dataset);
            RawFrames.LoadDataset(_dataset);
            Busmaster.LoadDataset(_dataset);
            SettingsDiagnostics.UpdateDataset(_dataset);

            StatusText = BuildStatusText(_dataset, Analysis.UseDownsampling, Analysis.MaxPointsPerTrace);
            SettingsDiagnostics.LastOperationSummary =
                $"Laatste verwerking: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"Duur: {stopwatch.Elapsed}\n" +
                $"Ruwe frames: {_dataset.RawCount:N0}\n" +
                $"Gedecodeerde meetpunten: {_dataset.DecodedSamples.Count:N0}\n" +
                $"Signalen: {_dataset.SignalCount:N0}";

            ProgressLabel = "Klaar.";
            ProgressValue = 100;

            PushRecent(LoadedSettings.RecentLogFiles, LogFilePath);
            PushRecent(LoadedSettings.RecentDbcFiles, DbcFilePath);
            try
            {
                await SaveSettingsAsync(CancellationToken.None);
                SettingsDiagnostics.ApplySettings(LoadedSettings, _settingsStore.SettingsPath);
            }
            catch (Exception settingsEx)
            {
                _logger.LogWarning(settingsEx, "Could not persist settings after successful load/decode.");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Laden/decoderen geannuleerd.";
            ProgressLabel = "Geannuleerd.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load/decode failed");
            SettingsDiagnostics.LastErrorDetails = ex.ToString();
            StatusText = "Laden/decoderen mislukt. Zie Diagnostics-tab voor details.";
            ProgressLabel = "Fout tijdens verwerking.";
            ProgressValue = 100;
            _messageDialogService.ShowError("Verwerking mislukt", ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            _loadCts?.Dispose();
            _loadCts = null;
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    private void CancelLoad()
    {
        _loadCts?.Cancel();
    }

    private async Task ExportDecodedCsvAsync()
    {
        if (_dataset is null)
        {
            return;
        }

        var filePath = _fileDialogService.SaveCsvFile(LogFilePath);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            await _csvExportService.ExportDecodedSignalsAsync(filePath, _dataset, CancellationToken.None);
            _messageDialogService.ShowInfo("CSV export", $"Gedecodeerde data opgeslagen:\n{filePath}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CSV export failed");
            _messageDialogService.ShowError("CSV export mislukt", ex.Message);
        }
    }

    private async Task ExportLayoutAsync()
    {
        await Analysis.ExportPresetCommand.ExecuteAsync(null);
        try
        {
            await SaveSettingsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist settings after layout export.");
        }
    }

    private async Task ImportLayoutAsync()
    {
        await Analysis.ImportPresetCommand.ExecuteAsync(null);
        try
        {
            await SaveSettingsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist settings after layout import.");
        }
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        SettingsDiagnostics.WriteBackToSettings(LoadedSettings);
        LoadedSettings.LastPlotViewOptions = Analysis.CaptureViewOptions();
        LoadedSettings.LastRawFrameFilter = RawFrames.CaptureFilterOptions();
        LoadedSettings.LastLogFilePath = LogFilePath;
        LoadedSettings.LastDbcFilePath = DbcFilePath;
        await _settingsStore.SaveAsync(LoadedSettings, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyProgramSettingsFromUiAsync()
    {
        try
        {
            SettingsDiagnostics.WriteBackToSettings(LoadedSettings);
            Analysis.ApplyViewOptions(LoadedSettings.LastPlotViewOptions);
            RawFrames.ApplyFilterOptions(LoadedSettings.LastRawFrameFilter);

            Analysis.ApplyGroupsCommand.Execute(null);
            RawFrames.ApplyFiltersCommand.Execute(null);

            await SaveSettingsAsync(CancellationToken.None);
            SettingsDiagnostics.ApplySettings(LoadedSettings, _settingsStore.SettingsPath);
            StatusText = _dataset is null
                ? "Programma-instellingen toegepast en opgeslagen."
                : BuildStatusText(_dataset, Analysis.UseDownsampling, Analysis.MaxPointsPerTrace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying settings from UI failed.");
            _messageDialogService.ShowError("Instellingen toepassen mislukt", ex.Message);
        }
    }

    private void UpdateCommandStates()
    {
        LoadAndDecodeCommand?.NotifyCanExecuteChanged();
        ExportDecodedCsvCommand?.NotifyCanExecuteChanged();
        CancelCommand?.NotifyCanExecuteChanged();
    }

    private static string BuildStatusText(CanDataset dataset, bool useDownsampling, int maxPointsPerTrace)
    {
        var speedModeText = useDownsampling
            ? $"LOD-weergave actief: maximaal {Math.Clamp(maxPointsPerTrace, 200, 200_000):N0} representatieve punten per trace; analyses en export gebruiken de bronreeks."
            : "Volledige zichtbare puntweergave actief; er wordt geen verborgen plotdecimator gebruikt.";

        var integrity = dataset.Completeness == DatasetCompleteness.Complete
            ? "Integriteit: COMPLETE"
            : "Integriteit: PARTIAL — analyses en exports zijn onvolledig";

        if (dataset.SignalCount > 0)
        {
            return
                $"Bestanden geladen.\n{integrity}\n" +
                $"Ruwe frames geparsed: {dataset.RawCount:N0}\n" +
                $"Extended frames: {dataset.ExtendedCount:N0}\n" +
                $"Gedecodeerde meetpunten: {dataset.DecodedSamples.Count:N0}\n" +
                $"Unieke signalen: {dataset.SignalCount:N0}\n" +
                $"Gedecodeerde berichten: {dataset.MessageSummaries.Count:N0}\n" +
                $"Niet-gematchte frames: {dataset.Diagnostics.UnmatchedFrameCount:N0}\n" +
                $"Niet-gematchte unieke IDs: {dataset.Diagnostics.UnmatchedUniqueIds:N0}\n" +
                $"Decode-/lengtefouten: {dataset.Diagnostics.DecodeErrorFrameCount:N0}\n" +
                $"Ambigue frames: {dataset.Diagnostics.AmbiguousFrameCount:N0}\n\n" +
                speedModeText;
        }

        return
            $"Bestanden geladen, maar er zijn geen DBC-signalen gedecodeerd.\n" +
            $"Ruwe frames geparsed: {dataset.RawCount:N0}\n" +
            $"Extended frames: {dataset.ExtendedCount:N0}\n" +
            $"DBC berichten: {dataset.Diagnostics.DbcMessageCount:N0}\n" +
            $"Niet-gematchte frames: {dataset.Diagnostics.UnmatchedFrameCount:N0}\n" +
            $"Niet-gematchte unieke IDs: {dataset.Diagnostics.UnmatchedUniqueIds:N0}\n\n" +
            $"Zie Diagnostics-tab voor onbekende IDs en decode-fallback details.\n" +
            speedModeText;
    }

    private static string FormatImportReport(ImportReport report)
    {
        var examples = report.Issues.Take(8)
            .Select(issue => $"- regel {issue.SourceLineNumber}: [{issue.Code}] {issue.Message}");
        return $"Parser: {report.ParserName}\n" +
               $"Totaal regels: {report.TotalLines:N0}\n" +
               $"Niet-dataregels: {report.NonDataLines:N0}\n" +
               $"Geaccepteerd: {report.AcceptedLines:N0}\n" +
               $"Afgewezen: {report.RejectedLines:N0}\n" +
               string.Join("\n", examples);
    }

    private static void PushRecent(List<string> list, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        list.RemoveAll(path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, filePath);
        const int max = 10;
        while (list.Count > max)
        {
            list.RemoveAt(list.Count - 1);
        }
    }
}
