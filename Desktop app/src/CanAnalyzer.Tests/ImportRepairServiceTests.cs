using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Parsing;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class ImportRepairServiceTests
{
    [Fact]
    public async Task RepairedCopyRemovesOnlyExplicitlyRejectedLogLines()
    {
        var directory = Directory.CreateTempSubdirectory("canalyser_repair_").FullName;
        var source = Path.Combine(directory, "source.log");
        var destination = Path.Combine(directory, "source.repaired.log");
        await File.WriteAllLinesAsync(source, ["header", "valid-a", "broken", "valid-b", "warning-source"]);
        var report = new ImportReport(
            "BUSMASTER",
            5,
            1,
            2,
            1,
            [
                new ImportIssue(ImportIssueSeverity.Error, "BUSMASTER_SYNTAX", "BUSMASTER", 3, "broken", "broken"),
                new ImportIssue(ImportIssueSeverity.Warning, "TIME_BACKWARDS", "BUSMASTER", 5, "warning", "warning-source"),
                new ImportIssue(ImportIssueSeverity.Error, "DBC_PARSE", "DBC", 4, "dbc", "valid-b")
            ]);

        try
        {
            var removed = await new ImportRepairService().CreateRepairedLogCopyAsync(
                source,
                destination,
                report,
                CancellationToken.None);

            Assert.Equal(1, removed);
            Assert.Equal(["header", "valid-a", "valid-b", "warning-source"], await File.ReadAllLinesAsync(destination));
            Assert.Equal(["header", "valid-a", "broken", "valid-b", "warning-source"], await File.ReadAllLinesAsync(source));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RepairRefusesToOverwriteOriginalOrGuessAtDbcErrors()
    {
        var path = Path.GetTempFileName();
        var report = new ImportReport(
            "test",
            1,
            0,
            1,
            0,
            [new ImportIssue(ImportIssueSeverity.Error, "DBC_PARSE", "DBC", 1, "bad DBC", "line")]);

        try
        {
            var service = new ImportRepairService();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateRepairedLogCopyAsync(
                path,
                path,
                report,
                CancellationToken.None));

            var destination = service.GetDefaultRepairedPath(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateRepairedLogCopyAsync(
                path,
                destination,
                report,
                CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
