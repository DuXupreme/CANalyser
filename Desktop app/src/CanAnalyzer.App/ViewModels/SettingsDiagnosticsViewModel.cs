using System.Collections.ObjectModel;
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

    public SettingsDiagnosticsViewModel()
    {
        ApplyProgramSettingsCommand = new AsyncRelayCommand(ApplyProgramSettingsAsync);
    }

    public ObservableCollection<string> RecentLogFiles { get; } = [];

    public ObservableCollection<string> RecentDbcFiles { get; } = [];

    public ObservableCollection<MessageSummary> MessageSummaries { get; } = [];

    public IAsyncRelayCommand ApplyProgramSettingsCommand { get; }

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

        MessageSummaries.Clear();
        foreach (var summary in dataset.MessageSummaries)
        {
            MessageSummaries.Add(summary);
        }
    }

    public void WriteBackToSettings(AppSettings settings)
    {
        settings.LastPlotViewOptions.MaxPointsPerTrace = Math.Clamp(DefaultMaxPointsPerTrace, 200, 20_000);
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
}
