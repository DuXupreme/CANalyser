using System.Text;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;

namespace CanAnalyzer.Core.Parsing;

public sealed class ImportRepairService : IImportRepairService
{
    private static readonly HashSet<string> RemovableIssueCodes = new(StringComparer.Ordinal)
    {
        "BUSMASTER_SYNTAX",
        "BUSMASTER_VALUE",
        "CANDUMP_SYNTAX",
        "CANDUMP_VALUE",
        "CSS_COLUMNS",
        "CSS_VALUE",
        "GENERIC_SYNTAX",
        "PEAK_SYNTAX",
        "PEAK_VALUE"
    };

    public bool IsRemovableLogIssue(ImportIssue issue)
    {
        return issue.Severity == ImportIssueSeverity.Error &&
               issue.SourceLineNumber > 0 &&
               RemovableIssueCodes.Contains(issue.Code);
    }

    public IReadOnlyList<ImportIssue> GetRemovableLogIssues(ImportReport report)
    {
        return report.Issues
            .Where(IsRemovableLogIssue)
            .ToArray();
    }

    public string GetDefaultRepairedPath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var candidate = Path.Combine(directory, $"{stem}.repaired{extension}");
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(directory, $"{stem}.repaired-{suffix}{extension}");
        }

        return candidate;
    }

    public async Task<int> CreateRepairedLogCopyAsync(
        string sourcePath,
        string destinationPath,
        ImportReport report,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Bronlogbestand niet gevonden.", sourcePath);
        }

        var sourceFullPath = Path.GetFullPath(sourcePath);
        var destinationFullPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("De gerepareerde kopie mag het originele logbestand niet overschrijven.");
        }

        var removableLines = GetRemovableLogIssues(report)
            .Select(static issue => issue.SourceLineNumber)
            .ToHashSet();
        if (removableLines.Count == 0)
        {
            throw new InvalidOperationException("Dit rapport bevat geen logregels die veilig automatisch kunnen worden verwijderd.");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationFullPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var temporaryPath = $"{destinationFullPath}.{Guid.NewGuid():N}.tmp";
        var removed = 0;
        try
        {
            {
                await using var sourceStream = new FileStream(
                    sourceFullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(sourceStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                await using var destinationStream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var writer = new StreamWriter(destinationStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                long lineNumber = 0;
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    lineNumber++;
                    if (removableLines.Contains(lineNumber))
                    {
                        removed++;
                        continue;
                    }

                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (removed != removableLines.Count)
            {
                throw new InvalidDataException(
                    $"Herstel is afgebroken: {removableLines.Count:N0} afgewezen regels verwacht, maar {removed:N0} gevonden.");
            }

            File.Move(temporaryPath, destinationFullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }

        return removed;
    }
}
