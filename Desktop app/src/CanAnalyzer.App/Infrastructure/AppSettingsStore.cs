using System.Text.Json;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using System.IO;
using System.Collections.Concurrent;

namespace CanAnalyzer.App.Infrastructure;

/// <inheritdoc />
public sealed class AppSettingsStore : IAppSettingsStore
{
    private const int CurrentSettingsVersion = 3;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SaveGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string, CancellationToken, Task> _writeText;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string SettingsPath { get; }

    public AppSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanAnalyzer", "settings.json"))
    {
    }

    public AppSettingsStore(string settingsPath) : this(settingsPath, File.WriteAllTextAsync) { }

    internal AppSettingsStore(string settingsPath, Func<string, string, CancellationToken, Task> writeText)
    {
        SettingsPath = Path.GetFullPath(settingsPath);
        _writeText = writeText;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return CreateCurrentSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            settings.RecentDbcFiles ??= [];
            settings.CsvExportPresets ??= [];
            settings.AnalysisProfiles ??= [];
            settings.SignalWatch ??= new();
            settings.SignalWatch.Signals ??= [];
            settings.RecentLogFiles ??= [];
            settings.LastPlotViewOptions ??= new CanAnalyzer.Core.Domain.PlotViewOptions();
            settings.LastPlotViewOptions.UseDownsampling = true;
            settings.LastPlotViewOptions.PlotOptions ??= [];
            settings.LastPlotViewOptions.PlotOptions.Remove("disable_downsampling");
            settings.LastRawFrameFilter ??= new CanAnalyzer.Core.Domain.RawFrameFilterOptions();
            settings.Telemetry ??= new TelemetryOptions();
            if (settings.SettingsVersion < CurrentSettingsVersion)
            {
                // Exact detail is temporary per window; every new session starts with automatic display sampling.
                settings.LastPlotViewOptions.UseDownsampling = true;
                settings.SettingsVersion = CurrentSettingsVersion;
            }
            if (string.IsNullOrWhiteSpace(settings.Telemetry.InstallationId))
            {
                settings.Telemetry.InstallationId = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(settings.Telemetry.EndpointUrl))
            {
                settings.Telemetry.EndpointUrl = TelemetryOptions.DefaultEndpointUrl;
            }

            if (string.IsNullOrWhiteSpace(settings.Telemetry.EndpointKey))
            {
                settings.Telemetry.EndpointKey = TelemetryOptions.DefaultEndpointKey;
            }

            settings.Telemetry.RetentionDays = Math.Clamp(settings.Telemetry.RetentionDays, 30, 730);
            return settings;
        }
        catch
        {
            return CreateCurrentSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var gate = SaveGates.GetOrAdd(SettingsPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        settings.SettingsVersion = CurrentSettingsVersion;
        var directory = Path.GetDirectoryName(SettingsPath)
                        ?? throw new InvalidOperationException("Settings path has no directory.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        var temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Complete the new file beside the current one before replacing it.
            // A failed write or cancellation must preserve saved presets and settings.
            await _writeText(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(SettingsPath))
                File.Replace(temporaryPath, SettingsPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, SettingsPath);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AppSettings CreateCurrentSettings() => new()
    {
        SettingsVersion = CurrentSettingsVersion
    };
}
