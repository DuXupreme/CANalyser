using System.Text.Json;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using System.IO;

namespace CanAnalyzer.App.Infrastructure;

/// <inheritdoc />
public sealed class AppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string SettingsPath { get; }

    public AppSettingsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanAnalyzer");
        SettingsPath = Path.Combine(root, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            settings.RecentDbcFiles ??= [];
            settings.RecentLogFiles ??= [];
            settings.LastPlotViewOptions ??= new CanAnalyzer.Core.Domain.PlotViewOptions();
            settings.LastRawFrameFilter ??= new CanAnalyzer.Core.Domain.RawFrameFilterOptions();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(SettingsPath)
                        ?? throw new InvalidOperationException("Settings path has no directory.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        await File.WriteAllTextAsync(SettingsPath, json, cancellationToken);
    }
}
