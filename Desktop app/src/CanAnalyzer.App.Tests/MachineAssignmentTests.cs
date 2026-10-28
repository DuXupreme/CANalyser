using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class MachineAssignmentTests
{
    private static readonly DateTimeOffset TestTransfer = new(2026, 10, 15, 11, 31, 19, TimeSpan.Zero);
    private static readonly JsonSerializerOptions HistoryJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Theory]
    [InlineData("2026-10-15T11:31:18.9999999Z", "Vlindermachine 1")]
    [InlineData("2026-10-15T11:31:19Z", "Vlindermachine 3")]
    [InlineData("2026-10-15T11:31:19.0000001Z", "Vlindermachine 3")]
    [InlineData("2026-10-15T13:31:18.9999999+02:00", "Vlindermachine 1")]
    [InlineData("2026-10-15T13:31:19+02:00", "Vlindermachine 3")]
    [InlineData("2026-10-15T06:31:19-05:00", "Vlindermachine 3")]
    public void SharedLogger_UsesMeasurementInstantAtHandover(string timestamp, string expected)
    {
        var measuredAt = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture);

        Assert.Equal(expected, OnlineMachineCatalog.ResolveName("48edfd35", measuredAt, TestTransfer));
    }

    [Theory]
    [InlineData("48EDFD35", "Vlindermachine 1")]
    [InlineData("22484aaa", "Vlindermachine 2")]
    [InlineData("unrecognized", "Onbekend")]
    [InlineData(null, "Onbekend")]
    public void PendingTransfer_DoesNotRelabelLogsWithMissingMeasurementTime(string? logger, string expected)
    {
        Assert.Equal(expected, OnlineMachineCatalog.ResolveName(logger, null, transferUtc: null));
    }

    [Fact]
    public void PendingTransfer_LeavesAllCurrentFilesOnMachineOneUntilCutoffIsConfirmed()
    {
        Assert.Equal("Vlindermachine 1", OnlineMachineCatalog.ResolveName("48EDFD35", TestTransfer.AddDays(-1), transferUtc: null));
        Assert.Equal("Vlindermachine 1", OnlineMachineCatalog.ResolveName("48EDFD35", TestTransfer.AddDays(1), transferUtc: null));
        Assert.Empty(OnlineMachineCatalog.ResolveExportMachine([Source(null), Source(TestTransfer)], transferUtc: null));
        Assert.Equal("Onbekend (logger 48EDFD35)", OnlineMachineCatalog.ResolveName("48EDFD35", null, TestTransfer));
    }

    [Fact]
    public void ActivatedTransfer_UsesConfirmedUtcCutoffAndNeverGuessesFromLoggerAlone()
    {
        Assert.Equal(TestTransfer, OnlineMachineCatalog.Machine3TransferUtc);
        Assert.Equal("Vlindermachine 1", OnlineMachineCatalog.ResolveName("48EDFD35", TestTransfer.AddTicks(-1)));
        Assert.Equal("Vlindermachine 3", OnlineMachineCatalog.ResolveName("48EDFD35", TestTransfer));
        Assert.Equal("Vlindermachine 3", OnlineMachineCatalog.ResolveName("48EDFD35", TestTransfer.AddDays(1)));
        Assert.Equal("Onbekend (logger 48EDFD35)", OnlineMachineCatalog.ResolveName("48EDFD35"));
        Assert.Empty(OnlineMachineCatalog.ResolveExportMachine([Source(null), Source(TestTransfer)]));
        Assert.Equal("Vlindermachine 3", OnlineMachineCatalog.ResolveExportMachine([Source(TestTransfer)]));
    }

    [Fact]
    public void Export_UsesOnlyAnUnambiguousMachineAcrossAllSourceFiles()
    {
        var old = Source(TestTransfer.AddDays(-1));
        var current = Source(TestTransfer);
        var unknown = Source(null);

        Assert.Equal("Vlindermachine 1", OnlineMachineCatalog.ResolveExportMachine([old, old with { Name = "00000002.MF4" }], TestTransfer));
        Assert.Equal("Vlindermachine 3", OnlineMachineCatalog.ResolveExportMachine([current], TestTransfer));
        Assert.Empty(OnlineMachineCatalog.ResolveExportMachine([old, current], TestTransfer));
        Assert.Empty(OnlineMachineCatalog.ResolveExportMachine([current, unknown], TestTransfer));
        Assert.Empty(OnlineMachineCatalog.ResolveExportMachine([unknown], TestTransfer));
        Assert.Empty(OnlineMachineCatalog.ResolveExportMachine([], TestTransfer));
        Assert.Empty(OnlineMachineCatalog.ResolveExportMachine([current, current with { Logger = "22484AAA" }], TestTransfer));
        Assert.Equal("Vlindermachine 2", OnlineMachineCatalog.ResolveExportMachine([unknown with { Logger = "22484AAA" }], TestTransfer));
    }

    [Fact]
    public void DatasetSourceRows_PreserveOriginalIdentityAndHistoricalMachineAssignment()
    {
        var old = Source(TestTransfer.AddTicks(-1));
        var current = old with { RecordedAt = TestTransfer };
        var oldRow = new DatasetSourceRow(old);
        var currentRow = new DatasetSourceRow(current);

        Assert.Equal("Vlindermachine 1", oldRow.Machine);
        Assert.Equal("Vlindermachine 3", currentRow.Machine);
        Assert.Equal(old.Name, currentRow.Name);
        Assert.Equal(old.SourcePath, currentRow.SourcePath);
        Assert.Equal(old.Logger, currentRow.Logger);
        Assert.Equal(old.Session, currentRow.Session);
        Assert.Equal("Onbekend (logger 48EDFD35)", new DatasetSourceRow(old with { RecordedAt = null }).Machine);
        Assert.Equal("Onbekend", new DatasetSourceRow(old with { Logger = null }).Machine);
    }

    [Theory]
    [InlineData("2026-10-06T10:00:00Z", "unverified")]
    [InlineData("2026-10-16T10:00:00Z", "unverified")]
    [InlineData("2026-10-06T10:00:00Z", "verified")]
    [InlineData("2026-10-16T10:00:00Z", "verified")]
    public void UnverifiedLoggerDateCannotProveEitherSideOfTransfer(string timestamp, string quality)
    {
        var source = Source(DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture)) with
        {
            ClockQuality = quality, ClockEvidence = null,
            UploadedAtUtc = TestTransfer.AddDays(4), ReportedMachineName = "Vlindermachine 1"
        };
        Assert.StartsWith("Onbekend (machine 1/3", OnlineMachineCatalog.ResolveName(source));
        Assert.StartsWith("Onbekend (machine 1/3", new DatasetSourceRow(source).Machine);
        Assert.Empty(OnlineMachineCatalog.ResolveExportMachine([source]));
    }

    [Fact]
    public void EmbeddedInstallationIdentifiesMachineEvenWithIncorrectLoggerDate()
    {
        var source = Source(TestTransfer.AddDays(-9)) with
        {
            ClockQuality = "suspect", ClockEvidence = "external-clock-mismatch",
            MachineAssignmentQuality = "embedded-installation", MachineId = "M03", InstallationId = "M03-20261015"
        };
        Assert.Equal("Vlindermachine 3", OnlineMachineCatalog.ResolveName(source));
        Assert.Equal("Vlindermachine 3", OnlineMachineCatalog.ResolveExportMachine([source]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingInstallationCannotVerifyMachine(string? installationId)
    {
        var source = Source(TestTransfer) with
        {
            ClockQuality = "unverified", ClockEvidence = null,
            MachineAssignmentQuality = "embedded-installation", MachineId = "M03", InstallationId = installationId
        };
        Assert.StartsWith("Onbekend", OnlineMachineCatalog.ResolveName(source));
        Assert.Empty(OnlineMachineCatalog.ResolveExportMachine([source]));
    }

    [Fact]
    public void DashboardInstallationClaimAloneDoesNotOverrideUnknownSourceMachine()
    {
        var source = Source(TestTransfer.AddDays(-9)) with
        {
            ClockQuality = "unverified", ClockEvidence = null,
            MachineAssignmentQuality = "verified-installation", MachineId = "M03", InstallationId = "api-claim"
        };
        Assert.StartsWith("Onbekend", OnlineMachineCatalog.ResolveName(source));
    }

    [Fact]
    public void VerifiedInstallationEvidenceOverridesAnUnreliableLoggerDate()
    {
        var source = Source(TestTransfer.AddDays(-9)) with
        {
            ClockQuality = "suspect", ClockEvidence = null,
            MachineAssignmentQuality = "verified-installation", MachineId = "M03", InstallationId = "I0001",
            MachineAssignmentEvidence = "Confirmed installation inventory, 2026-10-15"
        };
        Assert.Equal("Vlindermachine 3", OnlineMachineCatalog.ResolveName(source));
        Assert.Equal("Vlindermachine 3", new DatasetSourceRow(source).Machine);
        Assert.Equal("Vlindermachine 3", OnlineMachineCatalog.ResolveExportMachine([source]));
        Assert.Equal("Vlindermachine 3", OnlineMachineCatalog.ResolveExportMachine([source with { ClockQuality = "verified" }], TestTransfer));
    }

    [Theory]
    [InlineData(null, "M03", "I0001")]
    [InlineData(" ", "M03", "I0001")]
    [InlineData("inventory", null, "I0001")]
    [InlineData("inventory", " ", "I0001")]
    [InlineData("inventory", "M03", null)]
    [InlineData("inventory", "M03", " ")]
    public void VerifiedInstallationRequiresBothIdsAndEvidence(string? evidence, string? machineId, string? installationId)
    {
        var source = Source(TestTransfer) with
        {
            ClockQuality = "unverified", ClockEvidence = null,
            MachineAssignmentQuality = "verified-installation", MachineId = machineId, InstallationId = installationId,
            MachineAssignmentEvidence = evidence
        };
        Assert.StartsWith("Onbekend", OnlineMachineCatalog.ResolveName(source));
        Assert.Empty(OnlineMachineCatalog.ResolveExportMachine([source]));
    }

    [Fact]
    public void LegacyHistoryJson_RetainsMachineOneEvenWhenRestoredAfterHandover()
    {
        var entry = LegacyHistory();

        Assert.Null(entry.MachineId);
        Assert.Equal("48EDFD35", entry.EffectiveMachineId);
        Assert.Equal("Vlindermachine 1", entry.MachineName);
        Assert.Equal("48EDFD35/00000008/00000001.MF4", Assert.Single(entry.Files).Key);
        Assert.Contains("Vlindermachine 1", entry.DisplayName);
    }

    [Fact]
    public void NewHistoryJson_RoundTripsMachineThreeSeparatelyFromPhysicalLogger()
    {
        var entry = MachineThreeHistory();

        var json = JsonSerializer.Serialize(entry, HistoryJson);
        var restored = JsonSerializer.Deserialize<OnlineLogSelectionHistoryEntry>(json, HistoryJson)!;

        Assert.Equal("machine-3", restored.MachineId);
        Assert.Equal("machine-3", restored.EffectiveMachineId);
        Assert.Equal("48EDFD35", restored.LoggerId);
        Assert.Equal("Vlindermachine 3", restored.MachineName);
        Assert.Equal(entry.Files, restored.Files);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void LegacyHistoryWithoutMachineId_RetainsOriginalSelector(string? machineId)
    {
        Assert.Equal("48EDFD35", (LegacyHistory() with { MachineId = machineId }).EffectiveMachineId);
    }

    [Theory]
    [InlineData(false, "48EDFD35", "Vlindermachine 1")]
    [InlineData(true, "machine-3", "Vlindermachine 3")]
    public Task RestoreHistory_SelectsAndQueriesMachineIndependentlyOfSharedLogger(
        bool machineThree, string expectedSelector, string expectedName) => PlotRegressionTests.OnDispatcher(async () =>
    {
        var history = machineThree ? MachineThreeHistory() : LegacyHistory();
        var service = new RecordingLogService(history);
        var window = new OnlineLogsWindow(service, new SingleHistoryStore(history));
        try
        {
            var machines = Assert.IsType<ComboBox>(window.FindName("MachineBox"));
            Assert.Equal(3, machines.Items.Count);
            Assert.Equal(3, machines.Items.Cast<OnlineMachine>().Select(machine => machine.MachineId).Distinct().Count());
            // Start on the opposite machine to catch a logger-based restore choosing the first match.
            machines.SelectedValue = machineThree ? "48EDFD35" : "machine-3";

            Assert.IsType<Button>(window.FindName("RestoreSelectionButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Equal(expectedSelector, machines.SelectedValue);
            var selectedMachine = Assert.IsType<OnlineMachine>(machines.SelectedItem);
            Assert.Equal(expectedName, selectedMachine.Name);
            Assert.Equal("48EDFD35", selectedMachine.LoggerId);
            Assert.Equal(expectedSelector, Assert.Single(service.RequestedMachines));
            var row = Assert.Single(window.Rows);
            Assert.True(row.IsSelected);
            Assert.Equal(Assert.Single(history.Files).Key, row.Key);
            Assert.Equal(expectedName, row.Machine);
            Assert.Equal("Onbekend (machine 1/3)", row.MachineDisplay);
            Assert.Equal("Loggerklok onbevestigd", row.ClockDisplay);
        }
        finally { window.Close(); }
    });

    private static SourceLogFile Source(DateTimeOffset? recordedAt) =>
        SourceLogFile.FromArchiveEntry("48EDFD35/00000008/00000001.MF4") with
        {
            RecordedAt = recordedAt,
            ClockQuality = recordedAt.HasValue ? "verified" : "unverified",
            ClockEvidence = recordedAt.HasValue ? "explicit test time source" : null
        };

    private static OnlineLogSelectionHistoryEntry LegacyHistory() =>
        JsonSerializer.Deserialize<OnlineLogSelectionHistoryEntry>("""
        {
          "loggerId": "48EDFD35",
          "machineName": "Vlindermachine 1",
          "fromDate": "2026-10-01T00:00:00",
          "toDate": "2026-10-15T00:00:00",
          "selectedAtUtc": "2026-10-15T12:00:00Z",
          "files": [
            { "key": "48EDFD35/00000008/00000001.MF4", "name": "00000001.MF4",
              "logger": "48EDFD35", "session": "00000008", "sizeBytes": 42 }
          ]
        }
        """, HistoryJson)!;

    private static OnlineLogSelectionHistoryEntry MachineThreeHistory() => LegacyHistory() with
    {
        MachineId = "machine-3",
        MachineName = "Vlindermachine 3"
    };

    private sealed class SingleHistoryStore(OnlineLogSelectionHistoryEntry entry) : IOnlineLogSelectionHistoryStore
    {
        public IReadOnlyList<OnlineLogSelectionHistoryEntry> Load() => [entry];
        public Task RememberAsync(OnlineLogSelectionHistoryEntry value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLogService(OnlineLogSelectionHistoryEntry history) : IOnlineLogService
    {
        public List<string> RequestedMachines { get; } = [];
        public string? AuthenticationUserName => "test-user";
        public bool RememberCredentials => false;
        public void SetCredentials(string username, string password, bool rememberFor30Days = false) =>
            throw new NotSupportedException();
        public void ClearCredentials() => throw new NotSupportedException();
        public Task<OnlineLogQueryResult> GetLogsAsync(string loggerId, DateTimeOffset fromUtc,
            DateTimeOffset toUtc, CancellationToken cancellationToken)
        {
            RequestedMachines.Add(loggerId);
            var file = Assert.Single(history.Files);
            return Task.FromResult(new OnlineLogQueryResult(
                [new OnlineLogFile(file.Key, file.Name, history.MachineName, file.Logger, file.Session,
                    TestTransfer, DateTimeOffset.UtcNow, file.SizeBytes)], false, 100, 0));
        }
        public Task<string> DownloadArchiveAsync(IReadOnlyList<OnlineLogSelection> files,
            IProgress<OnlineDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
