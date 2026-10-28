using System.IO;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.State;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class PersistentAnalysisSettingsTests
{
    [Fact]
    public async Task ProfilesWatchAndExportChoicesSurviveRestartWhileExactDisplayRemainsTemporary()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var signal = new SignalIdentity("CAN2", CanFrameFormat.Classic, false, 123, "Joystick", "X");
        try
        {
            var settings = new AppSettings
            {
                MachineExportDirectory = "C:\\Analyses",
                MachineExportIncludeSamples = true,
                AnalysisProfiles = [new AnalysisProfile { Id = "confirmed", Version = 3, Confirmed = true,
                    DbcSha256 = new string('A', 64), SourceLogSha256 = new string('B', 64),
                    Joysticks = [new("joystick1", "Joystick 1", signal, signal with { SignalName = "Y" })] }],
                SignalWatch = new() { Signals = [signal] }
            };
            settings.LastPlotViewOptions.UseDownsampling = false;
            settings.LastPlotViewOptions.PlotOptions.Add("disable_downsampling");
            var store = new AppSettingsStore(file);
            await store.SaveAsync(settings, CancellationToken.None);
            var reopened = new AppSettingsStore(file).Load();
            Assert.Equal("confirmed", Assert.Single(reopened.AnalysisProfiles).Id);
            Assert.Equal(signal, Assert.Single(reopened.SignalWatch.Signals));
            Assert.Equal("C:\\Analyses", reopened.MachineExportDirectory);
            Assert.True(reopened.MachineExportIncludeSamples);
            Assert.True(reopened.LastPlotViewOptions.UseDownsampling);
            Assert.DoesNotContain("disable_downsampling", reopened.LastPlotViewOptions.PlotOptions);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void OlderSettingsRetainChoicesAndNormalizeNewCollections()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(file, """{"settingsVersion":2,"lastDbcFilePath":"known.dbc","analysisProfiles":null,"signalWatch":null,"lastPlotViewOptions":{"max_points":800,"use_downsampling":false}}""");
            var settings = new AppSettingsStore(file).Load();
            Assert.Equal("known.dbc", settings.LastDbcFilePath);
            Assert.Equal(800, settings.LastPlotViewOptions.MaxPointsPerTrace);
            Assert.True(settings.LastPlotViewOptions.UseDownsampling);
            Assert.Empty(settings.AnalysisProfiles);
            Assert.Empty(settings.SignalWatch.Signals);
        }
        finally { File.Delete(file); }
    }
}
