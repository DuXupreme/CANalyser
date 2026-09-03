using System.IO.Compression;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Storage;

namespace CanAnalyzer.Core.Parsing;

/// <summary>Imports CANedge MDF4 files and ZIP downloads as one chronological CAN timeline.</summary>
public sealed class Mdf4Parser(IMdf4ConversionService converter, PeakTrcParser peakParser) : ICanLogParser
{
    private const int MaximumArchiveFiles = 200;
    private const long MaximumArchiveFileBytes = 512L * 1024 * 1024;
    private const long MaximumArchiveBytes = 4L * 1024 * 1024 * 1024;

    public string Name => "CANedge MDF4";

    public int Probe(string filePath, IReadOnlyList<string> sampleLines)
    {
        _ = sampleLines;
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".mf4", StringComparison.OrdinalIgnoreCase) ? 100
            : extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ? 95
            : 0;
    }

    public async Task<CanLogParseResult?> ParseAsync(
        string filePath,
        ImportMode mode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), "CANalyser", "mf4-import", Guid.NewGuid().ToString("N"));
        var inputDirectory = Path.Combine(workDirectory, "input");
        var outputDirectory = Path.Combine(workDirectory, "output");
        Directory.CreateDirectory(inputDirectory);
        try
        {
            var inputPaths = Path.GetExtension(filePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? await ExtractArchiveAsync(filePath, inputDirectory, progress, cancellationToken).ConfigureAwait(false)
                : new[] { Path.GetFullPath(filePath) };
            var trcPaths = await converter.ConvertToPeakTrcAsync(inputPaths, outputDirectory, progress, cancellationToken)
                .ConfigureAwait(false);

            var parsedFiles = new List<(string Path, CanLogParseResult Result)>(trcPaths.Count);
            try
            {
                foreach (var trcPath in trcPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parsed = await peakParser.ParseAsync(trcPath, mode, progress, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"De geconverteerde log bevat geen CAN-frames: {Path.GetFileName(trcPath)}");
                    parsedFiles.Add((trcPath, parsed));
                }

                return MergeChronologically(parsedFiles, mode, progress, cancellationToken);
            }
            finally
            {
                foreach (var parsedFile in parsedFiles) (parsedFile.Result.Frames as IDisposable)?.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private static async Task<IReadOnlyList<string>> ExtractArchiveAsync(
        string archivePath,
        string inputDirectory,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(static entry => !string.IsNullOrEmpty(entry.Name) && entry.Name.EndsWith(".mf4", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length == 0) throw new InvalidDataException("Het ZIP-bestand bevat geen .MF4-logbestanden.");
        if (entries.Length > MaximumArchiveFiles)
            throw new InvalidDataException($"Het ZIP-bestand bevat meer dan {MaximumArchiveFiles:N0} MF4-bestanden. Maak een kleinere selectie.");

        long totalBytes = 0;
        var extracted = new List<string>(entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            if (entry.Length > MaximumArchiveFileBytes)
                throw new InvalidDataException($"MF4-bestand '{entry.Name}' is groter dan {MaximumArchiveFileBytes / 1024 / 1024:N0} MB.");
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumArchiveBytes)
                throw new InvalidDataException($"De uitgepakte MF4-data is groter dan {MaximumArchiveBytes / 1024 / 1024 / 1024:N0} GB.");

            var safeStem = SanitizeFileStem(Path.GetFileNameWithoutExtension(entry.Name));
            var destination = Path.Combine(inputDirectory, $"{index + 1:0000}-{safeStem}.mf4");
            await using var source = entry.Open();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, 128 * 1024, cancellationToken).ConfigureAwait(false);
            if (target.Length != entry.Length) throw new InvalidDataException($"MF4-bestand '{entry.Name}' is onvolledig uitgepakt.");
            extracted.Add(destination);
            progress?.Report(new LoadProgress($"ZIP uitpakken ({index + 1}/{entries.Length})...",
                1 + (int)Math.Round((index + 1) * 2d / entries.Length)));
        }

        return extracted;
    }

    private CanLogParseResult MergeChronologically(
        IReadOnlyList<(string Path, CanLogParseResult Result)> parsedFiles,
        ImportMode mode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (parsedFiles.Count == 0) throw new InvalidDataException("Er zijn geen geconverteerde MF4-bestanden om samen te voegen.");
        if (parsedFiles.Count > 1 && parsedFiles.Any(static file => file.Result.StartTimeUtc is null))
            throw new InvalidDataException("Minstens één MF4-deelbestand bevat geen geldige absolute starttijd; veilig samenvoegen is daardoor niet mogelijk.");

        var earliest = parsedFiles.Where(static file => file.Result.StartTimeUtc is not null)
            .Select(static file => file.Result.StartTimeUtc!.Value)
            .DefaultIfEmpty(DateTimeOffset.UnixEpoch)
            .Min();
        var ordered = parsedFiles.OrderBy(file => file.Result.StartTimeUtc ?? earliest)
            .ThenBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var merged = new DiskBackedFrameStore();
        var issues = new List<ImportIssue>();
        var cursors = new List<IEnumerator<RawCanFrame>>(ordered.Length);
        long frameIndex = 0;
        try
        {
            for (var fileIndex = 0; fileIndex < ordered.Length; fileIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (path, result) = ordered[fileIndex];
                var sourceName = Path.GetFileName(path);
                issues.AddRange(result.Report.Issues.Select(issue => issue with
                {
                    Parser = Name,
                    Message = $"{sourceName}: {issue.Message}"
                }));
                progress?.Report(new LoadProgress($"Tijdlijn samenvoegen ({fileIndex + 1}/{ordered.Length})...",
                    7 + (int)Math.Round((fileIndex + 1) * 3d / ordered.Length)));
            }

            var queue = new PriorityQueue<MergeCursor, (long Timestamp, int SourceIndex, long SourceFrameIndex)>();
            for (var sourceIndex = 0; sourceIndex < ordered.Length; sourceIndex++)
            {
                var result = ordered[sourceIndex].Result;
                var offset = result.StartTimeUtc is null ? 0L : checked((result.StartTimeUtc.Value - earliest).Ticks * 100L);
                var enumerator = result.Frames.GetEnumerator();
                cursors.Add(enumerator);
                if (!enumerator.MoveNext()) continue;
                var cursor = new MergeCursor(sourceIndex, offset, enumerator, enumerator.Current);
                queue.Enqueue(cursor, (checked(cursor.Frame.TimestampNanoseconds + offset), sourceIndex, cursor.Frame.FrameIndex));
            }

            while (queue.TryDequeue(out var cursor, out var priority))
            {
                cancellationToken.ThrowIfCancellationRequested();
                merged.Append(cursor.Frame with
                {
                    TimestampNanoseconds = priority.Timestamp,
                    FrameIndex = frameIndex++
                });
                if (!cursor.Enumerator.MoveNext()) continue;
                var next = cursor with { Frame = cursor.Enumerator.Current };
                queue.Enqueue(next, (checked(next.Frame.TimestampNanoseconds + next.Offset), next.SourceIndex, next.Frame.FrameIndex));
            }

            merged.Complete();
            var totalLines = parsedFiles.Sum(static file => file.Result.Report.TotalLines);
            var nonDataLines = parsedFiles.Sum(static file => file.Result.Report.NonDataLines);
            var acceptedLines = parsedFiles.Sum(static file => file.Result.Report.AcceptedLines);
            var rejectedLines = parsedFiles.Sum(static file => file.Result.Report.RejectedLines);
            var report = new ImportReport(Name, totalLines, nonDataLines, acceptedLines, rejectedLines, issues, mode);
            var completeness = parsedFiles.Any(static file => file.Result.Completeness == DatasetCompleteness.Partial) || report.HasErrors
                ? DatasetCompleteness.Partial
                : DatasetCompleteness.Complete;
            return new CanLogParseResult(merged, report, completeness, parsedFiles.Any(static file => file.Result.StartTimeUtc is not null) ? earliest : null);
        }
        catch
        {
            merged.Dispose();
            throw;
        }
        finally
        {
            foreach (var cursor in cursors) cursor.Dispose();
        }
    }

    private static string SanitizeFileStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrEmpty(result) ? "log" : result;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record MergeCursor(
        int SourceIndex,
        long Offset,
        IEnumerator<RawCanFrame> Enumerator,
        RawCanFrame Frame);
}
