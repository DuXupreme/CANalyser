using System.IO.Compression;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Storage;

namespace CanAnalyzer.Core.Parsing;

/// <summary>Imports CANedge MDF4 files and ZIP downloads as one chronological CAN timeline.</summary>
public sealed class Mdf4Parser(IMdf4ConversionService converter, PeakTrcParser peakParser) : ICanLogParser
{
    private const int MaximumArchiveFiles = Mdf4ImportLimits.MaximumFiles;
    private const long MaximumArchiveFileBytes = Mdf4ImportLimits.MaximumFileBytes;
    private const long MaximumArchiveBytes = Mdf4ImportLimits.MaximumBytes;

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
            var sourceFiles = new List<SourceLogFile>();
            var inputPaths = Path.GetExtension(filePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? await ExtractArchiveAsync(filePath, inputDirectory, sourceFiles, progress, cancellationToken).ConfigureAwait(false)
                : new[] { Path.GetFullPath(filePath) };
            if (sourceFiles.Count == 0) sourceFiles.Add(SourceLogFile.FromLocalFile(filePath));
            var sourceIndices = inputPaths.Select((path, index) => (Stem: Path.GetFileNameWithoutExtension(path), Index: index))
                .GroupBy(item => item.Stem, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Index, StringComparer.OrdinalIgnoreCase);
            var trcPaths = await converter.ConvertToPeakTrcAsync(inputPaths, outputDirectory, progress, cancellationToken)
                .ConfigureAwait(false);
            var outputSourceIndices = trcPaths.Select(path =>
                {
                    var stem = Path.GetFileNameWithoutExtension(path);
                    var matched = sourceIndices.TryGetValue(stem, out var index);
                    // The pinned CANedge converter appends _CAN to the input stem.
                    if (!matched && stem.EndsWith("_CAN", StringComparison.OrdinalIgnoreCase))
                        matched = sourceIndices.TryGetValue(stem[..^4], out index);
                    if (!matched && inputPaths.Count == 1 && trcPaths.Count == 1) { matched = true; index = 0; }
                    return (Path: path, Index: matched ? (int?)index : null);
                })
                .Where(item => item.Index.HasValue)
                .GroupBy(item => item.Index!.Value)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Single().Path, group => group.Key, StringComparer.OrdinalIgnoreCase);

            var parsedFiles = new List<(string Path, CanLogParseResult Result)>(trcPaths.Count);
            try
            {
                foreach (var trcPath in trcPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parsed = await peakParser.ParseAsync(trcPath, mode, progress, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"De geconverteerde log bevat geen CAN-frames: {Path.GetFileName(trcPath)}");
                    parsedFiles.Add((trcPath, parsed));
                    // Converter output order is not source order. Match the unique source
                    // basename, retaining unknown identity if a converter cannot prove it.
                    if (outputSourceIndices.TryGetValue(trcPath, out var sourceIndex))
                        sourceFiles[sourceIndex] = sourceFiles[sourceIndex] with { RecordedAt = FirstMeasurementTime(parsed) };
                }

                return MergeChronologically(parsedFiles, mode, progress, cancellationToken) with
                {
                    SourceFiles = sourceFiles.ToArray()
                };
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

    private static DateTimeOffset? FirstMeasurementTime(CanLogParseResult parsed)
    {
        if (parsed.StartTimeUtc is not { } origin || parsed.Frames.Count == 0) return null;
        var ticks = Math.DivRem(parsed.Frames[0].TimestampNanoseconds, 100, out var remainder);
        if (remainder < 0) ticks--;
        try { return origin.AddTicks(ticks); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static async Task<IReadOnlyList<string>> ExtractArchiveAsync(
        string archivePath,
        string inputDirectory,
        List<SourceLogFile> sourceFiles,
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
        ValidateArchiveSequence(entries);

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
            sourceFiles.Add(SourceLogFile.FromArchiveEntry(entry.FullName));
            progress?.Report(new LoadProgress($"ZIP uitpakken ({index + 1}/{entries.Length})...",
                1 + (int)Math.Round((index + 1) * 2d / entries.Length)));
        }

        return extracted;
    }

    private static void ValidateArchiveSequence(IReadOnlyList<ZipArchiveEntry> entries)
    {
        if (entries.Count <= 1) return;
        var identities = new List<OnlineLogPartIdentity>(entries.Count);
        foreach (var entry in entries)
        {
            var segments = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3)
            {
                throw new InvalidDataException(
                    "Dit ZIP-bestand bevat meerdere MF4-bestanden, maar de logger en sessie zijn niet betrouwbaar herkenbaar. " +
                    "Open één MF4-bestand, of maak via Online logs een selectie van opeenvolgende delen uit één sessie.");
            }
            identities.Add(new OnlineLogPartIdentity(segments[^3], segments[^2], segments[^1]));
        }

        var validation = OnlineLogSequencePolicy.Validate(identities);
        if (!validation.IsValid) throw new InvalidDataException(validation.Message);
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
            var ranges = ordered
                .Where(static file => file.Result.Frames.Count > 0)
                .Select(file =>
                {
                    var offset = file.Result.StartTimeUtc is null
                        ? 0L
                        : checked((file.Result.StartTimeUtc.Value - earliest).Ticks * 100L);
                    return (
                        Start: checked(offset + file.Result.Frames[0].TimestampNanoseconds),
                        End: checked(offset + file.Result.Frames[^1].TimestampNanoseconds));
                })
                .OrderBy(static range => range.Start)
                .ToArray();
            var gaps = new List<MeasurementGap>();
            if (ranges.Length > 0)
            {
                var end = ranges[0].End;
                foreach (var range in ranges.Skip(1))
                {
                    if (range.Start > end) gaps.Add(new(end / 1e9, range.Start / 1e9));
                    end = Math.Max(end, range.End);
                }
            }
            var report = new ImportReport(Name, totalLines, nonDataLines, acceptedLines, rejectedLines, issues, mode) { Gaps = gaps };
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
