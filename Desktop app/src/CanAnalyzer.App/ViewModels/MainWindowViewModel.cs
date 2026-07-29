using System.Diagnostics;
using System.ComponentModel;
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
    private readonly IActuatorCsvImportService _actuatorCsvImportService;
    private readonly IImportRepairService _importRepairService;
    private readonly IImportRepairWizardService _importRepairWizardService;
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageDialogService _messageDialogService;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ITelemetryService _telemetryService;
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
    private bool _isRepairWizardOpen;

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private string _progressLabel = "Klaar.";

    [ObservableProperty]
    private string _statusText = "Selecteer log + DBC en klik Laden/Decoderen.";

    [ObservableProperty]
    private bool _hasRepairablePartialImport;

    public MainWindowViewModel(
        ICanAnalysisPipeline analysisPipeline,
        ICsvExportService csvExportService,
        IActuatorCsvImportService actuatorCsvImportService,
        IImportRepairService importRepairService,
        IImportRepairWizardService importRepairWizardService,
        IFileDialogService fileDialogService,
        IMessageDialogService messageDialogService,
        IAppSettingsStore settingsStore,
        ITelemetryService telemetryService,
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
        _actuatorCsvImportService = actuatorCsvImportService;
        _importRepairService = importRepairService;
        _importRepairWizardService = importRepairWizardService;
        _fileDialogService = fileDialogService;
        _messageDialogService = messageDialogService;
        _settingsStore = settingsStore;
        _telemetryService = telemetryService;
        _logger = logger;

        Analysis = analysis;
        JoystickAnalytics = joystickAnalytics;
        RawFrames = rawFrames;
        Busmaster = busmaster;
        SettingsDiagnostics = settingsDiagnostics;
        DbcEditor = dbcEditor;

        Analysis.PropertyChanged += OnBackgroundOperationPropertyChanged;
        JoystickAnalytics.PropertyChanged += OnBackgroundOperationPropertyChanged;

        LoadedSettings = _settingsStore.Load();
        _telemetryService.Configure(LoadedSettings.Telemetry);
        LogFilePath = LoadedSettings.LastLogFilePath;
        DbcFilePath = LoadedSettings.LastDbcFilePath;
        Analysis.ApplyViewOptions(LoadedSettings.LastPlotViewOptions);
        RawFrames.ApplyFilterOptions(LoadedSettings.LastRawFrameFilter);
        SettingsDiagnostics.ApplySettings(LoadedSettings, _settingsStore.SettingsPath);
        SettingsDiagnostics.BindApplySettingsHandler(ApplyProgramSettingsFromUiAsync);

        BrowseLogFileCommand = new RelayCommand(BrowseLogFile);
        BrowseDbcFileCommand = new RelayCommand(BrowseDbcFile);
        LoadAndDecodeCommand = new AsyncRelayCommand(LoadAndDecodeAsync, CanLoadAndDecode);
        ImportActuatorCsvCommand = new AsyncRelayCommand(ImportActuatorCsvAsync, () => !IsAnyBusy);
        RepairPartialImportCommand = new AsyncRelayCommand(RepairPartialImportAsync, () => HasRepairablePartialImport && !IsAnyBusy);
        CancelCommand = new RelayCommand(CancelLoad, () => IsBusy);
        ExportDecodedCsvCommand = new AsyncRelayCommand(ExportDecodedCsvAsync, CanExportDecodedCsv);
        ExportLayoutCommand = new AsyncRelayCommand(ExportLayoutAsync);
        ImportLayoutCommand = new AsyncRelayCommand(ImportLayoutAsync);
        UpdateCommandStates();
        _ = _telemetryService.TrackEventAsync("app_started", new Dictionary<string, object?>
        {
            ["telemetry_configured"] = LoadedSettings.Telemetry.Enabled,
            ["app_version_display"] = SettingsDiagnostics.AppVersion
        });
    }

    public AppSettings LoadedSettings { get; }

    public AnalysisViewModel Analysis { get; }

    public JoystickAnalyticsViewModel JoystickAnalytics { get; }

    public RawFramesViewModel RawFrames { get; }

    public BusmasterViewModel Busmaster { get; }

    public SettingsDiagnosticsViewModel SettingsDiagnostics { get; }

    public DbcEditorViewModel DbcEditor { get; }

    public bool IsAnyBusy => IsBusy || IsBackgroundAnalysisBusy;

    public bool IsBusyIndicatorVisible => (IsBusy && !IsRepairWizardOpen) || IsBackgroundAnalysisBusy;

    public bool IsBackgroundAnalysisBusy => Analysis.IsBusy || JoystickAnalytics.IsBusy;

    public string ActiveProgressLabel => IsBusy
        ? ProgressLabel
        : Analysis.IsBusy
            ? Analysis.BusyLabel
            : JoystickAnalytics.IsBusy
                ? JoystickAnalytics.BusyLabel
                : ProgressLabel;

    public IRelayCommand BrowseLogFileCommand { get; }

    public IRelayCommand BrowseDbcFileCommand { get; }

    public IAsyncRelayCommand LoadAndDecodeCommand { get; }

    public IAsyncRelayCommand ImportActuatorCsvCommand { get; }

    public IAsyncRelayCommand RepairPartialImportCommand { get; }

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

    partial void OnIsBusyChanged(bool value)
    {
        NotifyBusyStateChanged();
    }

    partial void OnIsRepairWizardOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusyIndicatorVisible));
    }

    partial void OnHasRepairablePartialImportChanged(bool value)
    {
        RepairPartialImportCommand?.NotifyCanExecuteChanged();
    }

    partial void OnProgressLabelChanged(string value)
    {
        OnPropertyChanged(nameof(ActiveProgressLabel));
    }

    private void OnBackgroundOperationPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(AnalysisViewModel.IsBusy) or nameof(AnalysisViewModel.BusyLabel))
        {
            NotifyBusyStateChanged();
        }
    }

    private void NotifyBusyStateChanged()
    {
        OnPropertyChanged(nameof(IsAnyBusy));
        OnPropertyChanged(nameof(IsBusyIndicatorVisible));
        OnPropertyChanged(nameof(IsBackgroundAnalysisBusy));
        OnPropertyChanged(nameof(ActiveProgressLabel));
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
        return !IsAnyBusy &&
               !string.IsNullOrWhiteSpace(LogFilePath) &&
               !string.IsNullOrWhiteSpace(DbcFilePath);
    }

    private bool CanExportDecodedCsv()
    {
        return !IsAnyBusy && _dataset is not null;
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
        HasRepairablePartialImport = false;
        ProgressValue = 0;
        ProgressLabel = "Start verwerking...";
        SettingsDiagnostics.LastErrorDetails = string.Empty;
        _loadCts = new CancellationTokenSource();
        UpdateCommandStates();

        var importMode = ImportMode.Strict;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<LoadProgress>(item =>
            {
                ProgressLabel = item.Label;
                ProgressValue = Math.Clamp(item.Percent, 0, 100);
            });

            var loadResult = await LoadWithRepairWizardAsync(LogFilePath, DbcFilePath, progress, _loadCts.Token);
            if (loadResult is null)
            {
                StatusText = "Import geblokkeerd wegens integriteitsfouten.";
                ProgressLabel = "Herstel afgebroken.";
                return;
            }

            _dataset = loadResult.Dataset;
            importMode = loadResult.Mode;
            LogFilePath = loadResult.LogPath;
            DbcFilePath = loadResult.DbcPath;
            HasRepairablePartialImport =
                _dataset.Completeness == DatasetCompleteness.Partial &&
                _dataset.ImportReport?.HasErrors == true;

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
                    HasRepairablePartialImport = false;
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

            _ = _telemetryService.TrackEventAsync("load_decode_completed", new Dictionary<string, object?>
            {
                ["duration_ms"] = stopwatch.ElapsedMilliseconds,
                ["duration_bucket"] = TelemetryBuckets.DurationMilliseconds(stopwatch.ElapsedMilliseconds),
                ["import_mode"] = importMode.ToString(),
                ["dataset_completeness"] = _dataset.Completeness.ToString(),
                ["raw_frame_bucket"] = TelemetryBuckets.Count(_dataset.RawCount),
                ["extended_frame_bucket"] = TelemetryBuckets.Count(_dataset.ExtendedCount),
                ["decoded_sample_bucket"] = TelemetryBuckets.Count(_dataset.DecodedSamples.Count),
                ["signal_bucket"] = TelemetryBuckets.Count(_dataset.SignalCount),
                ["message_bucket"] = TelemetryBuckets.Count(_dataset.MessageSummaries.Count),
                ["unmatched_frame_bucket"] = TelemetryBuckets.Count(_dataset.Diagnostics.UnmatchedFrameCount),
                ["decode_error_bucket"] = TelemetryBuckets.Count(_dataset.Diagnostics.DecodeErrorFrameCount)
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = "Laden/decoderen geannuleerd.";
            ProgressLabel = "Geannuleerd.";
            _ = _telemetryService.TrackEventAsync("load_decode_cancelled", new Dictionary<string, object?>
            {
                ["duration_ms"] = stopwatch.ElapsedMilliseconds,
                ["duration_bucket"] = TelemetryBuckets.DurationMilliseconds(stopwatch.ElapsedMilliseconds)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load/decode failed");
            SettingsDiagnostics.LastErrorDetails = ex.ToString();
            StatusText = "Laden/decoderen mislukt. Zie Diagnostics-tab voor details.";
            ProgressLabel = "Fout tijdens verwerking.";
            ProgressValue = 100;
            _messageDialogService.ShowError("Verwerking mislukt", ex.Message);
            _ = _telemetryService.TrackEventAsync("load_decode_failed", new Dictionary<string, object?>
            {
                ["duration_ms"] = stopwatch.ElapsedMilliseconds,
                ["duration_bucket"] = TelemetryBuckets.DurationMilliseconds(stopwatch.ElapsedMilliseconds),
                ["exception_type"] = ex.GetType().Name
            });
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

    private async Task ImportActuatorCsvAsync()
    {
        _messageDialogService.ShowInfo(
            "Actuator CSV import",
            "WAT IS DIT?\n" +
            "Een Actuator Testbench CSV bevat de ruwe telemetrie van één meting: tijd, mode, doel- en werkelijke positie, fout, PWM, stroom, spanning, vermogen en faults. " +
            "CANalyser gebruikt de getallen uit de CSV; een PNG is alleen een afbeelding en kan niet interactief worden geanalyseerd.\n\n" +
            "WAAR KOMT HET BESTAND VANDAAN?\n" +
            "Maak het in de Actuator Testbench met START LOGGING → voer de test uit → STOP LOGGING → Bewaren + grafieken maken.\n" +
            "Het bestand staat daarna in:\n" +
            "<Actuator-Testbench-repository>\\logs\\actuator_YYYY-MM-DD_HH-MM-SS.csv\n\n" +
            "Als de Actuator Testbench van Git is gecloned, is dit de logs-map in die lokale clone. Meetlogs worden normaal niet naar Git gecommit; voer lokaal een meting uit of kopieer de gewenste CSV naar je computer.\n\n" +
            "WELK FORMAAT?\n" +
            "Selecteer het .csv-bestand, niet de gelijknamige .json of _graphs.png. De eerste regel bevat kolomnamen en getallen gebruiken een punt als decimaalteken. Verplichte gegevens zijn onder andere arduino_time_ms, mode, target_position_pct, actual_position_pct, pwm en current_a.\n\n" +
            "MEERDERE RUNS VERGELIJKEN\n" +
            "Selecteer met Ctrl/Shift meerdere actuator_*.csv-bestanden. Iedere run krijgt zijn bestandsnaam in de legenda en de eerste STEP-doelwijziging wordt uitgelijnd op t=0. Negatieve tijd is de aanloop vóór de stap.");

        var files = _fileDialogService.PickActuatorCsvFiles(LogFilePath);
        if (files.Count == 0) return;

        IsBusy = true;
        _dataset?.Dispose();
        _dataset = null;
        HasRepairablePartialImport = false;
        ProgressValue = 0;
        ProgressLabel = "Actuator CSV-runs voorbereiden...";
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
            _dataset = await _actuatorCsvImportService.ImportAsync(files, progress, _loadCts.Token);

            Analysis.LoadDataset(_dataset);
            LoadDefaultActuatorComparisonGroups(_dataset);
            JoystickAnalytics.LoadDataset(_dataset);
            RawFrames.LoadDataset(_dataset);
            Busmaster.LoadDataset(_dataset);
            SettingsDiagnostics.UpdateDataset(_dataset);
            SettingsDiagnostics.LogFilePath = string.Join(" | ", files.Select(Path.GetFileName));
            SettingsDiagnostics.DbcFilePath = "(niet nodig voor Actuator CSV)";

            StatusText =
                $"Actuator CSV-vergelijking geladen: {files.Count} run(s), {_dataset.SignalCount:N0} signalen.\n" +
                "Tijdas is per run uitgelijnd op de eerste STEP-doelovergang (t=0). " +
                "Gebruik legenda, cursors en gekoppelde X-assen om runs te vergelijken.";
            SettingsDiagnostics.LastOperationSummary =
                $"Laatste verwerking: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"Type: Actuator CSV vergelijking\nDuur: {stopwatch.Elapsed}\n" +
                $"Runs: {files.Count:N0}\nSignalen: {_dataset.SignalCount:N0}";
            ProgressLabel = "Actuator-runs klaar voor vergelijking.";
            ProgressValue = 100;
            var importWarnings = _dataset.ImportReport?.Issues
                .Where(static issue => issue.Severity == ImportIssueSeverity.Warning)
                .Select(static issue => $"• {issue.Message}")
                .ToArray() ?? [];
            if (importWarnings.Length > 0)
            {
                _messageDialogService.ShowInfo(
                    "Actuator CSV: controle van brondata",
                    "CANalyser heeft de CSV geladen, maar vond het volgende:\n\n" +
                    string.Join("\n", importWarnings));
            }
            foreach (var file in files) PushRecent(LoadedSettings.RecentLogFiles, file);
            await SaveSettingsAsync(CancellationToken.None);

            _ = _telemetryService.TrackEventAsync("actuator_csv_comparison_loaded", new Dictionary<string, object?>
            {
                ["run_count"] = files.Count,
                ["signal_count"] = _dataset.SignalCount,
                ["duration_ms"] = stopwatch.ElapsedMilliseconds
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = "Actuator CSV-import geannuleerd.";
            ProgressLabel = "Geannuleerd.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Actuator CSV import failed");
            SettingsDiagnostics.LastErrorDetails = ex.ToString();
            StatusText = "Actuator CSV-import mislukt. Zie Diagnostics-tab voor details.";
            ProgressLabel = "Fout tijdens Actuator CSV-import.";
            ProgressValue = 100;
            _messageDialogService.ShowError("Actuator CSV-import mislukt", ex.Message);
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

    private async Task RepairPartialImportAsync()
    {
        var report = _dataset?.ImportReport;
        if (!HasRepairablePartialImport || report is null ||
            string.IsNullOrWhiteSpace(LogFilePath) || string.IsNullOrWhiteSpace(DbcFilePath))
        {
            return;
        }

        ProgressLabel = "Foutrapport voorbereiden...";
        IsBusy = true;
        IsRepairWizardOpen = true;
        ImportRepairWizardResult repair;
        try
        {
            repair = await _importRepairWizardService.ShowAsync(
                report,
                LogFilePath,
                DbcFilePath,
                CancellationToken.None);
        }
        finally
        {
            IsRepairWizardOpen = false;
            IsBusy = false;
        }

        if (repair.Decision != ImportRepairDecision.RetryStrict)
        {
            return;
        }

        if (repair.RemoveRejectedLogLines)
        {
            IsBusy = true;
            ProgressValue = 0;
            ProgressLabel = "Veilige, opgeschoonde logkopie maken...";
            _loadCts = new CancellationTokenSource();
            try
            {
                var removed = await _importRepairService.CreateRepairedLogCopyAsync(
                    LogFilePath,
                    repair.LogPath,
                    report,
                    _loadCts.Token);
                ProgressLabel = $"{removed:N0} afgewezen regel(s) verwijderd.";
            }
            catch (OperationCanceledException)
            {
                ProgressLabel = "Herstel geannuleerd.";
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "Could not create repaired CAN log copy from an existing partial import.");
                _messageDialogService.ShowError("Logherstel mislukt", ex.Message);
                return;
            }
            finally
            {
                _loadCts?.Dispose();
                _loadCts = null;
                IsBusy = false;
            }
        }

        LogFilePath = repair.LogPath;
        DbcFilePath = repair.DbcPath;
        await LoadAndDecodeAsync();
    }

    private async Task<ImportLoadResult?> LoadWithRepairWizardAsync(
        string initialLogPath,
        string initialDbcPath,
        IProgress<LoadProgress> progress,
        CancellationToken cancellationToken)
    {
        var activeLogPath = initialLogPath;
        var activeDbcPath = initialDbcPath;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var dataset = await _analysisPipeline.LoadAsync(
                    activeLogPath,
                    activeDbcPath,
                    ImportMode.Strict,
                    progress,
                    cancellationToken);
                return new ImportLoadResult(dataset, ImportMode.Strict, activeLogPath, activeDbcPath);
            }
            catch (ImportIntegrityException integrityException)
            {
                SettingsDiagnostics.LastErrorDetails = FormatImportReport(integrityException.Report);
                ProgressLabel = "STRICT-validatie vond herstelbare problemen.";

                ImportRepairWizardResult repair;
                IsRepairWizardOpen = true;
                try
                {
                    repair = await _importRepairWizardService.ShowAsync(
                        integrityException.Report,
                        activeLogPath,
                        activeDbcPath,
                        cancellationToken);
                }
                finally
                {
                    IsRepairWizardOpen = false;
                }
                if (repair.Decision == ImportRepairDecision.Cancel)
                {
                    return null;
                }

                if (repair.Decision == ImportRepairDecision.ContinuePartial)
                {
                    ProgressLabel = "Bewust laden in PARTIAL-modus...";
                    var partialDataset = await _analysisPipeline.LoadAsync(
                        repair.LogPath,
                        repair.DbcPath,
                        ImportMode.Partial,
                        progress,
                        cancellationToken);
                    return new ImportLoadResult(
                        partialDataset,
                        ImportMode.Partial,
                        repair.LogPath,
                        repair.DbcPath);
                }

                if (repair.RemoveRejectedLogLines)
                {
                    try
                    {
                        ProgressLabel = "Veilige, opgeschoonde logkopie maken...";
                        var removed = await _importRepairService.CreateRepairedLogCopyAsync(
                            activeLogPath,
                            repair.LogPath,
                            integrityException.Report,
                            cancellationToken);
                        activeLogPath = repair.LogPath;
                        ProgressLabel = $"{removed:N0} afgewezen regel(s) verwijderd; STRICT opnieuw controleren...";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                    {
                        _logger.LogWarning(ex, "Could not create repaired CAN log copy.");
                        _messageDialogService.ShowError("Logherstel mislukt", ex.Message);
                        continue;
                    }
                }
                else
                {
                    activeLogPath = repair.LogPath;
                }

                activeDbcPath = repair.DbcPath;
            }
        }
    }

    private void LoadDefaultActuatorComparisonGroups(CanDataset dataset)
    {
        Analysis.FrameIdFilter = null;
        Analysis.TimeStart = null;
        Analysis.TimeEnd = null;
        var definitions = new (string Title, string[] Signals)[]
        {
            ("Positie-overlay", ["TargetPositionPct", "ActualPositionPct"]),
            ("Positiefout", ["PositionErrorPct"]),
            ("PWM", ["Pwm"]),
            ("Voedingsstroom", ["CurrentA", "FilteredCurrentA"]),
            ("Busspanning", ["BusVoltageV"]),
            ("Vermogen", ["PowerW"])
        };
        var groups = definitions.Select(definition => new PlotGroup
        {
            Title = definition.Title,
            Signals = dataset.SignalSeriesByLabel.Values
                .Where(series => definition.Signals.Contains(series.Identity.SignalName, StringComparer.Ordinal))
                .Select(static series => series.Label)
                .ToList()
        }).Where(static group => group.Signals.Count > 0).ToArray();
        Analysis.LoadPlotGroups(groups);
        Analysis.ApplyGroupsCommand.Execute(null);
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
            _ = _telemetryService.TrackEventAsync("export_decoded_csv", new Dictionary<string, object?>
            {
                ["decoded_sample_bucket"] = TelemetryBuckets.Count(_dataset.DecodedSamples.Count),
                ["signal_bucket"] = TelemetryBuckets.Count(_dataset.SignalCount),
                ["dataset_completeness"] = _dataset.Completeness.ToString()
            });
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
            _telemetryService.Configure(LoadedSettings.Telemetry);
            Analysis.ApplyViewOptions(LoadedSettings.LastPlotViewOptions);
            RawFrames.ApplyFilterOptions(LoadedSettings.LastRawFrameFilter);

            Analysis.ApplyGroupsCommand.Execute(null);
            RawFrames.ApplyFiltersCommand.Execute(null);

            await SaveSettingsAsync(CancellationToken.None);
            SettingsDiagnostics.ApplySettings(LoadedSettings, _settingsStore.SettingsPath);
            StatusText = _dataset is null
                ? "Programma-instellingen toegepast en opgeslagen."
                : BuildStatusText(_dataset, Analysis.UseDownsampling, Analysis.MaxPointsPerTrace);
            _ = _telemetryService.TrackEventAsync("settings_applied", new Dictionary<string, object?>
            {
                ["telemetry_enabled"] = LoadedSettings.Telemetry.Enabled,
                ["telemetry_endpoint_configured"] = !string.IsNullOrWhiteSpace(LoadedSettings.Telemetry.EndpointUrl),
                ["default_use_downsampling"] = LoadedSettings.LastPlotViewOptions.UseDownsampling,
                ["default_link_x_axis"] = LoadedSettings.LastPlotViewOptions.LinkXAxisAcrossPanels,
                ["default_max_points_per_trace"] = LoadedSettings.LastPlotViewOptions.MaxPointsPerTrace
            });
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
        ImportActuatorCsvCommand?.NotifyCanExecuteChanged();
        RepairPartialImportCommand?.NotifyCanExecuteChanged();
        ExportDecodedCsvCommand?.NotifyCanExecuteChanged();
        CancelCommand?.NotifyCanExecuteChanged();
    }

    private static string BuildStatusText(CanDataset dataset, bool useDownsampling, int maxPointsPerTrace)
    {
        var speedModeText = useDownsampling
            ? $"LOD/downsampling actief: de grafiek tekent maximaal {Math.Clamp(maxPointsPerTrace, 200, 200_000):N0} representatieve punten per trace om grote logs soepel te tonen. " +
              "Dit verandert de brondata, decode, analyse en CSV-export niet. Implicatie: visuele details tussen representatieve punten kunnen minder exact lijken; zoom in of schakel LOD uit voor detailinspectie."
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

    private sealed record ImportLoadResult(
        CanDataset Dataset,
        ImportMode Mode,
        string LogPath,
        string DbcPath);
}
