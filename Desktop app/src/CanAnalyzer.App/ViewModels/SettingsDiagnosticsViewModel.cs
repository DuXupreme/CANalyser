using System.Collections.ObjectModel;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using CanAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanAnalyzer.App.ViewModels;

/// <summary>
/// Settings/diagnostics tab view model.
/// </summary>
public sealed partial class SettingsDiagnosticsViewModel : ObservableObject
{
    private readonly IUpdateService _updateService;
    private readonly IMessageDialogService _messageDialogService;
    private Func<Task>? _applySettingsAsync;

    [ObservableProperty]
    private string? _logFilePath;

    [ObservableProperty]
    private string? _dbcFilePath;

    [ObservableProperty]
    private string _lastOperationSummary = "Nog geen verwerking uitgevoerd.";

    [ObservableProperty]
    private string _decodeDiagnostics = string.Empty;

    [ObservableProperty]
    private string _integritySummary = "Nog geen dataset geladen.";

    [ObservableProperty]
    private string _lastErrorDetails = string.Empty;

    [ObservableProperty]
    private string _settingsFilePath = string.Empty;

    [ObservableProperty]
    private int _defaultMaxPointsPerTrace = 4000;

    [ObservableProperty]
    private int _defaultSubplotHeight = 280;

    [ObservableProperty]
    private int _defaultSignalListHeight = 420;

    [ObservableProperty]
    private bool _defaultUseDownsampling = true;

    [ObservableProperty]
    private bool _defaultShowLegend = true;

    [ObservableProperty]
    private int _defaultRawMaxRows = 50_000;

    [ObservableProperty]
    private string _settingsSaveStatus = string.Empty;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    public SettingsDiagnosticsViewModel(IUpdateService updateService, IMessageDialogService messageDialogService)
    {
        _updateService = updateService;
        _messageDialogService = messageDialogService;
        ApplyProgramSettingsCommand = new AsyncRelayCommand(ApplyProgramSettingsAsync);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
    }

    /// <summary>Versie van de draaiende app, voor weergave.</summary>
    public string AppVersion => _updateService.CurrentVersion;

    public ObservableCollection<string> RecentLogFiles { get; } = [];

    public ObservableCollection<string> RecentDbcFiles { get; } = [];

    public ObservableCollection<MessageSummary> MessageSummaries { get; } = [];

    public IAsyncRelayCommand ApplyProgramSettingsCommand { get; }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public void BindApplySettingsHandler(Func<Task> applySettingsAsync)
    {
        _applySettingsAsync = applySettingsAsync;
    }

    public void ApplySettings(AppSettings settings, string settingsPath)
    {
        LogFilePath = settings.LastLogFilePath;
        DbcFilePath = settings.LastDbcFilePath;
        SettingsFilePath = settingsPath;

        RecentLogFiles.Clear();
        foreach (var item in settings.RecentLogFiles)
        {
            RecentLogFiles.Add(item);
        }

        RecentDbcFiles.Clear();
        foreach (var item in settings.RecentDbcFiles)
        {
            RecentDbcFiles.Add(item);
        }

        DefaultMaxPointsPerTrace = settings.LastPlotViewOptions.MaxPointsPerTrace;
        DefaultSubplotHeight = settings.LastPlotViewOptions.SubplotHeight;
        DefaultSignalListHeight = settings.LastPlotViewOptions.SignalListHeight;
        DefaultUseDownsampling = settings.LastPlotViewOptions.UseDownsampling;
        DefaultShowLegend = settings.LastPlotViewOptions.ShowLegend;
        DefaultRawMaxRows = settings.LastRawFrameFilter.MaxRows <= 0 ? 50_000 : settings.LastRawFrameFilter.MaxRows;
    }

    public void UpdateDataset(CanDataset dataset)
    {
        DecodeDiagnostics = dataset.Diagnostics.DecodeNote;
        var report = dataset.ImportReport;
        IntegritySummary =
            $"DATASETSTATUS: {dataset.Completeness.ToString().ToUpperInvariant()}\n" +
            $"Appversie: {dataset.ApplicationVersion}\n" +
            $"Bron SHA-256: {dataset.SourceLogSha256}\n" +
            $"DBC SHA-256: {dataset.DbcSha256}\n" +
            (report is null
                ? "Importverslag: niet beschikbaar"
                : $"Parser: {report.ParserName}\nImportmodus: {report.Mode.ToString().ToUpperInvariant()}\n" +
                  $"Regels: {report.TotalLines:N0} = non-data {report.NonDataLines:N0} + geaccepteerd {report.AcceptedLines:N0} + afgewezen {report.RejectedLines:N0}\n" +
                  $"Invariant geldig: {(report.IsConsistent ? "JA" : "NEE")}\n" +
                  $"Diagnoses: {report.Issues.Count:N0} ({report.Issues.Count(static issue => issue.Severity == ImportIssueSeverity.Error):N0} fouten)");

        MessageSummaries.Clear();
        foreach (var summary in dataset.MessageSummaries)
        {
            MessageSummaries.Add(summary);
        }
    }

    public void WriteBackToSettings(AppSettings settings)
    {
        settings.LastPlotViewOptions.MaxPointsPerTrace = Math.Clamp(DefaultMaxPointsPerTrace, 200, 200_000);
        settings.LastPlotViewOptions.SubplotHeight = Math.Clamp(DefaultSubplotHeight, 160, 1200);
        settings.LastPlotViewOptions.SignalListHeight = Math.Clamp(DefaultSignalListHeight, 180, 1500);
        settings.LastPlotViewOptions.UseDownsampling = DefaultUseDownsampling;
        settings.LastPlotViewOptions.ShowLegend = DefaultShowLegend;
        settings.LastRawFrameFilter.MaxRows = Math.Clamp(DefaultRawMaxRows, 1, 2_000_000);
    }

    private async Task ApplyProgramSettingsAsync()
    {
        if (_applySettingsAsync is null)
        {
            SettingsSaveStatus = "Instellingenhandler ontbreekt.";
            return;
        }

        await _applySettingsAsync();
        SettingsSaveStatus = $"Instellingen opgeslagen ({DateTime.Now:HH:mm:ss}).";
    }

    private async Task CheckForUpdatesAsync()
    {
        if (!_updateService.IsInstalled)
        {
            UpdateStatus = "Updates zijn alleen beschikbaar in de geïnstalleerde versie.";
            return;
        }

        UpdateStatus = "Bezig met controleren op updates...";
        var result = await _updateService.CheckForUpdatesAsync();

        if (result.Error is not null)
        {
            UpdateStatus = $"Controle mislukt: {result.Error}";
            return;
        }

        if (!result.UpdateAvailable)
        {
            UpdateStatus = $"Je hebt de nieuwste versie ({_updateService.CurrentVersion}).";
            return;
        }

        var confirmed = _messageDialogService.Confirm(
            "Update beschikbaar",
            $"Versie {result.NewVersion} is beschikbaar (huidige versie {_updateService.CurrentVersion}).\n\n" +
            "Nu downloaden en de app herstarten?");
        if (!confirmed)
        {
            UpdateStatus = $"Update {result.NewVersion} beschikbaar, nog niet geïnstalleerd.";
            return;
        }

        try
        {
            UpdateStatus = "Bezig met downloaden van de update...";
            await _updateService.DownloadAndApplyAsync();
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update mislukt: {ex.Message}";
        }
    }
}
