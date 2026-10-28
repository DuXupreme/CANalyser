using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.State;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class MachineExportShellTests
{
    [Fact]
    public Task PublicExportCommandKeepsFullDatasetResultsWhenScreenSelectionsChange()
        => PlotRegressionTests.OnDispatcher(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "canalyser-shell-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = Path.Combine(directory, "synthetic.trc");
            var dbc = Path.Combine(directory, "synthetic.dbc");
            File.WriteAllText(log, "Synthetic shell fixture; parsing is supplied by the test pipeline.");
            File.WriteAllText(dbc, "Synthetic shell fixture; decoding is supplied by the test pipeline.");
            var report = new ImportReport("TRC", 2, 0, 1, 1,
                [new(ImportIssueSeverity.Error, "invalid_frame", "TRC", 2, "Rejected fixture row", "invalid")], ImportMode.Partial);
            using var dataset = Dataset(report,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(log))),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dbc))));
            var recognized = AnalysisProfileResolver.Recognize(dataset);
            var profile = recognized with
            {
                Id = "saved-machine-profile", Confirmed = true,
                Joysticks = recognized.Joysticks.Select(joystick => joystick with
                {
                    CalibrationConfirmed = true, XCalibration = new(-100, 0, 100), YCalibration = new(-100, 0, 100)
                }).ToArray()
            };
            var profileBefore = JsonSerializer.Serialize(profile, AnalysisBundleExporter.JsonOptions);
            var settings = new MemorySettings(new()
            {
                LastLogFilePath = log, LastDbcFilePath = dbc, MachineExportDirectory = directory,
                AnalysisProfiles = [profile]
            });
            var telemetry = new AnalyticsRegressionTests.Telemetry();
            var dialogs = new RecordingDialogs();
            var files = new UnexpectedFileDialogs();
            var limits = new DisplayLimits();
            var active = new ActiveUsageViewModel(telemetry);
            using var analysis = new AnalysisViewModel(new PlotModelBuilder(), null!, null!, null!,
                new XAxisSyncService(), telemetry, active, NullLogger<AnalysisViewModel>.Instance, limits);
            var joystick = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, active);
            var pipeline = new SyntheticPipeline(dataset);
            var repairWizard = new RecordingRepairWizard();
            using var shell = new MainWindowViewModel(pipeline, null!, new ActuatorCsvImportService(), null!, repairWizard, files, null!, dialogs,
                settings, telemetry, analysis, joystick, new RawFramesViewModel(new RawFrameFilterService(), limits),
                new BusmasterViewModel(limits, telemetry),
                new SettingsDiagnosticsViewModel(new NoUpdates(), dialogs, telemetry, limits), null!,
                NullLogger<MainWindowViewModel>.Instance);

            try
            {
                // Exercise the public shell load path. Only parse/decode is synthetic; no reflection or real source logs.
                await shell.LoadAndDecodeCommand.ExecuteAsync(null);
                await DrainDisplayWork(shell);
                Assert.Equal(1, pipeline.Preparations);
                Assert.Empty(dialogs.Errors);
                Assert.Equal(2, joystick.AvailableJoystickProfiles.Count);
                Assert.True(shell.ExportAnalysesCommand.CanExecute(null));
                Assert.False(shell.LoadedSettings.MachineExportIncludeSamples);

                // Choosing another source does not load it. Make that state explicit and
                // prevent exporting the previous recording under the newly chosen name.
                var loadedTitle = shell.WindowTitle;
                Assert.Contains(shell.ApplicationVersion, loadedTitle);
                Assert.False(shell.HasPendingSourceSelection);
                shell.LogFilePath = Path.Combine(directory, "next-recording.trc");
                Assert.True(shell.HasPendingSourceSelection);
                Assert.True(shell.ShowSourceSelectionNotice);
                Assert.Contains("Nieuwe selectie nog niet geladen", shell.SourceSelectionNotice);
                Assert.Equal(loadedTitle, shell.WindowTitle);
                Assert.False(shell.ExportAnalysesCommand.CanExecute(null));
                Assert.False(shell.ExportDecodedCsvCommand.CanExecute(null));
                Assert.True(shell.HasRepairablePartialImport);
                Assert.False(shell.RepairPartialImportCommand.CanExecute(null));
                // Calling ExecuteAsync directly must also reject a report/source mismatch.
                await shell.RepairPartialImportCommand.ExecuteAsync(null);
                Assert.Single(repairWizard.Calls);
                await shell.ExportAnalysesCommand.ExecuteAsync(null);
                Assert.Empty(Directory.GetFiles(directory, "*.zip"));
                shell.KeepLoadedSourceSelectionCommand.Execute(null);
                Assert.Equal(log, shell.LogFilePath);
                Assert.False(shell.HasPendingSourceSelection);
                Assert.True(shell.ExportAnalysesCommand.CanExecute(null));
                Assert.True(shell.RepairPartialImportCommand.CanExecute(null));
                shell.DbcFilePath = Path.Combine(directory, "different.dbc");
                Assert.True(shell.HasPendingSourceSelection);
                Assert.False(shell.ExportAnalysesCommand.CanExecute(null));
                Assert.False(shell.RepairPartialImportCommand.CanExecute(null));
                await shell.RepairPartialImportCommand.ExecuteAsync(null);
                Assert.Single(repairWizard.Calls);
                shell.KeepLoadedSourceSelectionCommand.Execute(null);
                Assert.Equal(dbc, shell.DbcFilePath);
                Assert.True(shell.ExportAnalysesCommand.CanExecute(null));
                Assert.Equal(1, pipeline.Preparations);
                await shell.RepairPartialImportCommand.ExecuteAsync(null);
                Assert.Equal((log, dbc), repairWizard.Calls.Last());
                Assert.Equal(2, repairWizard.Calls.Count);

                await shell.ExportAnalysesCommand.ExecuteAsync(null);
                Assert.Empty(dialogs.Errors);
                var firstPath = Assert.Single(Directory.GetFiles(directory, "*.zip"));
                var first = ReadExport(firstPath);

                analysis.TimeStart = 2;
                analysis.TimeEnd = 4;
                analysis.FrameIdFilter = "0x10";
                analysis.UseDownsampling = false;
                joystick.UseJoystickTimeWindow = true;
                joystick.JoystickTimeStartSeconds = 3;
                joystick.JoystickTimeEndSeconds = 5;
                joystick.SelectedJoystickProfile = joystick.AvailableJoystickProfiles.Last();
                joystick.SelectedReactionActuator = "Rechts";
                joystick.SelectedReactionReference = "Setpointwijziging → reactie";
                joystick.SelectedReactionKind = "Correcties en omkeren";
                shell.Busmaster.ApplySignalWatchOptions(new()
                {
                    Signals = [dataset.SignalSeriesByIdentity.Keys.Last()], LatestValues = true, OnlyWatched = true
                });
                shell.Busmaster.SetWatchCursorAt(8);
                shell.SelectedMainTabIndex = 3;
                await DrainDisplayWork(shell);
                Assert.Equal(2, analysis.TimeStart);
                Assert.Equal(4, analysis.TimeEnd);
                Assert.Equal("Rechts", joystick.SelectedReactionActuator);
                Assert.Same(joystick.AvailableJoystickProfiles.Last(), joystick.SelectedJoystickProfile);
                Assert.True(shell.ExportAnalysesCommand.CanExecute(null));

                await shell.ExportAnalysesCommand.ExecuteAsync(null);
                Assert.Empty(dialogs.Errors);
                var exports = Directory.GetFiles(directory, "*.zip");
                Assert.Equal(2, exports.Length);
                var secondPath = Assert.Single(exports, path => path != firstPath);
                Assert.NotEqual(Path.GetFileName(firstPath), Path.GetFileName(secondPath));
                var second = ReadExport(secondPath);
                foreach (var entry in first.Keys) Assert.Equal(first[entry], second[entry]);
                Assert.Equal(profileBefore, JsonSerializer.Serialize(shell.LoadedSettings.AnalysisProfiles.Single(), AnalysisBundleExporter.JsonOptions));

                using var run = JsonDocument.Parse(second["analyses.json"]);
                Assert.Equal("FullDataset", run.RootElement.GetProperty("Scope").GetString());
                Assert.Equal(0, run.RootElement.GetProperty("StartSeconds").GetDouble());
                Assert.Equal(10, run.RootElement.GetProperty("EndSeconds").GetDouble());
                Assert.Equal(2, run.RootElement.GetProperty("Joysticks").GetArrayLength());
                Assert.Equal(2, run.RootElement.GetProperty("Actuators").GetArrayLength());
                var signalMetrics = AnalysisBundleComparisonReader.Read(secondPath).Metrics.Where(metric => metric.Key.StartsWith("signal:")).ToArray();
                Assert.Equal(10, signalMetrics.Length);
                Assert.All(signalMetrics, metric => Assert.Equal(10, metric.Denominator, 6));
                Assert.True(settings.Saved.Count >= 3); // successful load and both successful exports
                Assert.All(settings.Saved, saved => Assert.Equal(directory, saved.GetProperty("MachineExportDirectory").GetString()));
                Assert.Equal(directory, shell.SettingsDiagnostics.MachineExportDirectory);
                Assert.Equal(2, dialogs.Information.Count(message => message.Title == "Machine-analyse opgeslagen"));
                Assert.Empty(dialogs.Confirmations);
                Assert.Equal(0, files.Calls);
                Assert.False(shell.IsBusy);

                // Dedicated CSV import replaces the loaded source identity and cannot reuse the old CAN DBC.
                var csv = Path.Combine(directory, "actuator-run.csv");
                File.WriteAllLines(csv,
                [
                    "pc_timestamp,arduino_time_ms,mode,command_position_pct,target_position_pct,actual_position_pct,error_pct,pwm,current_a,filtered_current_a,peak_current_a,bus_voltage_v,shunt_voltage_mv,power_w,fault_code,fault_text,fault_latched,lower_limit,upper_limit,estop",
                    "2026-01-01T12:00:00.000,1000,AUTO,50,25,25,0,100,1.2,1.1,2.0,24.0,18.0,28.8,0,No fault,0,0,0,0",
                    "2026-01-01T12:00:00.100,1100,AUTO,50,25,25,0,100,1.2,1.1,2.0,24.0,18.0,28.8,0,No fault,0,0,0,0",
                    "2026-01-01T12:00:00.200,1200,AUTO,50,85,25,60,100,1.2,1.1,2.0,24.0,18.0,28.8,0,No fault,0,0,0,0",
                    "2026-01-01T12:00:00.300,1300,AUTO,50,85,40,45,100,1.2,1.1,2.0,24.0,18.0,28.8,0,No fault,0,0,0,0"
                ]);
                files.ActuatorFiles = [csv];
                await shell.ImportActuatorCsvCommand.ExecuteAsync(null);
                await DrainDisplayWork(shell);
                Assert.Empty(dialogs.Errors);
                Assert.Equal(csv, shell.LogFilePath);
                Assert.Null(shell.DbcFilePath);
                Assert.Contains("actuator-run.csv", shell.LoadedSourceSummary);
                Assert.Contains("Actuator CSV (geen DBC)", shell.LoadedSourceSummary);
                Assert.DoesNotContain("synthetic", shell.LoadedSourceSummary);
                Assert.False(shell.HasPendingSourceSelection);
                Assert.False(shell.LoadAndDecodeCommand.CanExecute(null));
                shell.LogFilePath = log;
                shell.DbcFilePath = dbc;
                Assert.True(shell.HasPendingSourceSelection);
                shell.KeepLoadedSourceSelectionCommand.Execute(null);
                Assert.Equal(csv, shell.LogFilePath);
                Assert.Null(shell.DbcFilePath);
                Assert.False(shell.HasPendingSourceSelection);
                Assert.False(shell.LoadAndDecodeCommand.CanExecute(null));
            }
            finally { await DrainDisplayWork(shell); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private static async Task DrainDisplayWork(MainWindowViewModel shell)
    {
        // Cancel pending debounces as well as active work, keeping this test independent of timer latency.
        await shell.Analysis.CancelAndWaitAsync();
        await shell.JoystickAnalytics.CancelAndWaitAsync();
        await shell.Analysis.ActiveUsage.CancelAndWaitAsync();
        await shell.Busmaster.CancelAndWaitAsync();
    }

    private static Dictionary<string, string> ReadExport(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        Assert.Null(zip.GetEntry("samples.csv"));
        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        Assert.Equal("FullDataset", manifest.RootElement.GetProperty("AnalysisScope").GetString());
        Assert.False(manifest.RootElement.GetProperty("Options").GetProperty("IncludeSamples").GetBoolean());
        return new[] { "analyses.json", "signal_statistics.csv", "signal_minutes.csv", "signals.json" }
            .ToDictionary(name => name, name =>
            {
                using var reader = new StreamReader(zip.GetEntry(name)!.Open());
                return reader.ReadToEnd();
            });
    }

    private static CanDataset Dataset(ImportReport? report = null, string? logHash = null, string? dbcHash = null)
    {
        var identities = new[]
        {
            Id(1, "JoystickLeft", "pos_x"), Id(1, "JoystickLeft", "pos_y"),
            Id(2, "JoystickRight", "pos_x"), Id(2, "JoystickRight", "pos_y"),
            Id(3, "ActuatorLeft", "RealPos"), Id(3, "ActuatorLeft", "ActualPos"),
            Id(4, "ActuatorRight", "RealPos"), Id(4, "ActuatorRight", "ActualPos"),
            Id(5, "Drive", "RPM"), Id(6, "Battery", "SOC")
        };
        var frames = new List<RawCanFrame>();
        var samples = new List<DecodedSignalSample>();
        for (var index = 0; index <= 100; index++)
        {
            var ns = index * 100_000_000L;
            foreach (var identity in identities)
            {
                var value = identity.SignalName == "SOC" ? 80 - index * .01
                    : identity.SignalName == "RPM" ? index is >= 20 and < 80 ? 500 : 0
                    : identity.SignalName == "ActualPos" ? Math.Clamp(index - 21, 0, 30)
                    : identity.SignalName == "RealPos" ? Math.Clamp(index - 20, 0, 30)
                    : identity.SignalName == "pos_x" ? index is >= 20 and < 70 ? 50 : 0 : 0;
                var unit = identity.SignalName is "ActualPos" or "RealPos" ? "mm" : identity.SignalName == "RPM" ? "rpm" : "%";
                var frameIndex = frames.Count;
                frames.Add(new(ns, identity.FrameId, 8, new byte[8], "Rx", "1", false, frameIndex));
                samples.Add(new(ns, frameIndex, -1, identity, value, BigInteger.Zero, unit));
            }
        }
        return new DatasetBuilder().Build(frames, samples, [], new(0, 0, 0, 0, 0, "synthetic shell export"),
            importReport: report, completeness: report is null ? DatasetCompleteness.Complete : DatasetCompleteness.Partial,
            sourceLogSha256: logHash ?? new string('A', 64), dbcSha256: dbcHash ?? new string('B', 64));
        static SignalIdentity Id(uint frameId, string message, string signal) => new("1", CanFrameFormat.Classic, false, frameId, message, signal);
    }

    private sealed class SyntheticPipeline(CanDataset dataset) : ICanAnalysisPipeline
    {
        public int Preparations { get; private set; }
        public Task<PreparedCanAnalysis> PrepareForReviewAsync(string log, string dbc, IProgress<LoadProgress>? progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Preparations++; return Task.FromResult(new PreparedCanAnalysis(dataset)); }
        public Task<PreparedCanAnalysis> RetryForReviewAsync(PreparedCanAnalysis previous, string log, string dbc, IProgress<LoadProgress>? progress, CancellationToken token)
            => throw new InvalidOperationException("The complete synthetic input must not need repair.");
        public Task<CanDataset> LoadAsync(string log, string dbc, ImportMode mode, IProgress<LoadProgress>? progress, CancellationToken token)
            => throw new InvalidOperationException("The complete synthetic input must use the prepared load path.");
    }

    private sealed class MemorySettings(AppSettings settings) : IAppSettingsStore
    {
        public string SettingsPath => string.Empty;
        public List<JsonElement> Saved { get; } = [];
        public AppSettings Load() => settings;
        public Task SaveAsync(AppSettings value, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Saved.Add(JsonSerializer.SerializeToElement(value)); return Task.CompletedTask; }
    }

    private sealed class RecordingDialogs : IMessageDialogService
    {
        public List<(string Title, string Message)> Information { get; } = [];
        public List<(string Title, string Message)> Errors { get; } = [];
        public List<(string Title, string Message)> Confirmations { get; } = [];
        public void ShowInfo(string title, string message) => Information.Add((title, message));
        public void ShowError(string title, string message) => Errors.Add((title, message));
        public bool Confirm(string title, string message) { Confirmations.Add((title, message)); return false; }
    }

    private sealed class UnexpectedFileDialogs : IFileDialogService
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string>? ActuatorFiles { get; set; }
        private string? Unexpected() { Calls++; throw new InvalidOperationException("Export with a saved directory must not open file dialogs."); }
        public string? PickLogFile(string? initialPath) => Unexpected();
        public IReadOnlyList<string> PickActuatorCsvFiles(string? initialPath) { if (ActuatorFiles is not null) return ActuatorFiles; Unexpected(); return []; }
        public string? PickDbcFile(string? initialPath) => Unexpected();
        public string? SaveDbcFile(string? initialPath) => Unexpected();
        public string? PickPresetFile(string? initialPath) => Unexpected();
        public string? SavePresetFile(string? initialPath) => Unexpected();
        public string? SaveCsvFile(string? initialPath) => Unexpected();
    }

    private sealed class RecordingRepairWizard : IImportRepairWizardService
    {
        public List<(string Log, string Dbc)> Calls { get; } = [];
        public Task<ImportRepairWizardResult> ShowAsync(ImportReport report, string logPath, string dbcPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add((logPath, dbcPath));
            return Task.FromResult(new ImportRepairWizardResult(
                Calls.Count == 1 ? ImportRepairDecision.ContinuePartial : ImportRepairDecision.Cancel,
                false, logPath, dbcPath));
        }
    }

    private sealed class NoUpdates : IUpdateService
    {
        public string CurrentVersion => "0.0.0-test";
        public bool IsInstalled => false;
        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadAndApplyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
