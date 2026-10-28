using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
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
        using var cacheSession = AnalysisCacheStorage.CreateSession(Path.Combine(AnalysisCacheStorage.DefaultDirectory, "mf4-import"));
        var workDirectory = cacheSession.DirectoryPath;
        var inputDirectory = Path.Combine(workDirectory, "input");
        var outputDirectory = Path.Combine(workDirectory, "output");
        Directory.CreateDirectory(inputDirectory);
        try
        {
            var sourceFiles = new List<SourceLogFile>();
            var omissions = new List<OnlineLogArchiveOmission>();
            var inputPaths = Path.GetExtension(filePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? await ExtractArchiveAsync(filePath, inputDirectory, sourceFiles, omissions, progress, cancellationToken).ConfigureAwait(false)
                : new[] { Path.GetFullPath(filePath) };
            if (sourceFiles.Count == 0) sourceFiles.Add(SourceLogFile.FromLocalFile(filePath) with { SizeBytes = new FileInfo(filePath).Length });
            for (var index = 0; index < inputPaths.Count; index++)
            {
                await using var input = File.OpenRead(inputPaths[index]);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                if (sourceFiles[index].Sha256 is { Length: > 0 } expectedHash &&
                    !string.Equals(expectedHash, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Een MF4-bestand wijkt af van de SHA-256 in het logmanifest.");
                sourceFiles[index] = Mdf4MetadataReader.Read(inputPaths[index], sourceFiles[index] with { Sha256 = hash });
            }
            var installations = sourceFiles
                .Where(source => source.MachineAssignmentQuality is "embedded-installation" or "verified-installation" &&
                                 source.MachineId is not null && source.InstallationId is not null)
                .Select(source => source.MachineId + "/" + source.InstallationId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            if (installations.Length > 1)
                throw new InvalidDataException("De selectie bevat meerdere bewezen machine-installaties. Open iedere installatie afzonderlijk.");
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
                    {
                        var recordedAt = FirstMeasurementTime(parsed);
                        var source = sourceFiles[sourceIndex];
                        // PEAK's OLE date origin has millisecond precision. Never transfer
                        // a verified claim to different recording bytes/time by overwriting it.
                        if (source.ClockQuality == "verified" &&
                            (source.RecordedAt is not { } claimed || recordedAt is not { } actual ||
                             (claimed - actual).Duration() > TimeSpan.FromMilliseconds(1)))
                            throw new InvalidDataException("De geverifieerde tijd in het logmanifest past niet bij de eerste opname in het MF4-bestand.");
                        sourceFiles[sourceIndex] = source with { RecordedAt = recordedAt };
                    }
                }

                ValidateSourceChronology(parsedFiles, outputSourceIndices, sourceFiles);
                return MergeChronologically(parsedFiles, mode, progress, outputSourceIndices, sourceFiles, omissions, cancellationToken) with
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
            // The session keeps its ownership lock until conversion and all parsing have finished.
            // Dispose then removes only this owned temporary directory.
            cacheSession.Dispose();
        }
    }

    private static void ValidateSourceChronology(
        IReadOnlyList<(string Path, CanLogParseResult Result)> parsedFiles,
        IReadOnlyDictionary<string, int> sourceIndices, IReadOnlyList<SourceLogFile> sources)
    {
        var mapped = parsedFiles.Where(file => sourceIndices.ContainsKey(file.Path))
            .Select(file => (Source: sources[sourceIndices[file.Path]], file.Result)).ToArray();
        foreach (var session in mapped.Where(file => file.Source.Logger is not null && file.Source.Session is not null)
                     .GroupBy(file => (file.Source.Logger, file.Source.Session)))
        {
            DateTimeOffset? previous = null;
            foreach (var file in session.OrderBy(file => OnlineLogSequencePolicy.TryParsePartNumber(file.Source.Name, out var part) ? part : int.MaxValue))
            {
                if (file.Result.StartTimeUtc is not { } current) continue;
                if (previous is { } prior && current < prior)
                    throw new InvalidDataException("De loggerklok loopt achteruit tussen opeenvolgende MF4-delen. Open de ononderbroken delen afzonderlijk; automatisch sorteren zou de klokbreuk verbergen.");
                previous = current;
            }
        }
        foreach (var logger in mapped.Where(file => file.Source.Logger is not null &&
                     uint.TryParse(file.Source.Session, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                     .GroupBy(file => file.Source.Logger, StringComparer.OrdinalIgnoreCase))
        {
            DateTimeOffset? previous = null;
            var sessions = logger.GroupBy(file => uint.Parse(file.Source.Session!, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .OrderBy(group => group.Key);
            foreach (var session in sessions)
            {
                var first = session.OrderBy(file => OnlineLogSequencePolicy.TryParsePartNumber(file.Source.Name, out var part) ? part : int.MaxValue)
                    .First().Result.StartTimeUtc;
                if (first is not { } current) continue;
                if (previous is { } prior && current < prior)
                    throw new InvalidDataException("De loggerklok loopt achteruit tussen oplopende sessienummers. Open de ononderbroken sessies afzonderlijk; automatisch sorteren zou de klokbreuk verbergen.");
                previous = current;
            }
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
        List<OnlineLogArchiveOmission> omissions,
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
        var manifestEntries = archive.Entries.Where(entry => string.Equals(entry.FullName,
            OnlineLogArchiveManifestCodec.EntryName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifestEntries.Length > 1) throw new InvalidDataException("Het ZIP-bestand bevat meerdere logmanifesten.");
        OnlineLogArchiveManifest? manifest = null;
        if (manifestEntries.Length == 1)
        {
            await using var manifestStream = manifestEntries[0].Open();
            manifest = await OnlineLogArchiveManifestCodec.ReadAsync(manifestStream, cancellationToken).ConfigureAwait(false);
            var actualNames = entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (actualNames.Count != entries.Length || manifest.Files.Count != entries.Length ||
                manifest.Files.Any(file => !actualNames.Contains(file.ArchiveName)))
                throw new InvalidDataException("Logmanifest en MF4-bestanden komen niet één-op-één overeen.");
            omissions.AddRange(manifest.OmittedFiles);
        }
        // V2 validates included + explicitly omitted objects as one sequence. V1
        // and archives without a manifest must still contain every intermediate file.
        if (manifest?.SchemaVersion != 2) ValidateArchiveSequence(entries);

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
            var identity = SourceLogFile.FromArchiveEntry(entry.FullName) with { SizeBytes = entry.Length };
            if (manifest is not null)
            {
                var metadata = manifest.Files.Single(file => string.Equals(file.ArchiveName, entry.FullName, StringComparison.OrdinalIgnoreCase));
                if (metadata.SizeBytes != entry.Length) throw new InvalidDataException("MF4-bestandsgrootte wijkt af van het logmanifest.");
                identity = identity with
                {
                    Logger = metadata.LoggerId,
                    Session = metadata.Session,
                    RecordedAt = metadata.RawRecordedAtUtc,
                    UploadedAtUtc = metadata.UploadedAtUtc,
                    Sha256 = metadata.Sha256,
                    ObjectKey = metadata.ObjectKey,
                    ObjectVersion = metadata.ObjectVersion,
                    MetadataRevision = metadata.MetadataRevision ?? manifest.MetadataRevision,
                    ClockQuality = metadata.ClockQuality,
                    ClockEvidence = metadata.ClockEvidence,
                    ReportedMachineName = metadata.MachineName,
                    MachineAssignmentEvidence = metadata.MachineAssignmentEvidence,
                    MachineId = metadata.MachineAssignmentQuality == "verified-installation" ? metadata.MachineId : null,
                    InstallationId = metadata.MachineAssignmentQuality == "verified-installation" ? metadata.InstallationId : null,
                    ConfigurationId = metadata.MachineAssignmentQuality == "verified-installation" ? metadata.ConfigurationId : null,
                    MachineAssignmentQuality = metadata.MachineAssignmentQuality == "verified-installation" ? "verified-installation" : "unknown"
                };
            }
            sourceFiles.Add(identity);
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
        IReadOnlyDictionary<string, int> sourceIndices,
        IReadOnlyList<SourceLogFile> sources,
        IReadOnlyList<OnlineLogArchiveOmission> omissions,
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
        // CANedge reserves bus 9 for internal diagnostic traffic. The same bus
        // number in a generic PEAK file is deliberately not treated as internal.
        var coverage = new MeasurementCoverage(["9"]);
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
                var frame = cursor.Frame with
                {
                    TimestampNanoseconds = priority.Timestamp,
                    FrameIndex = frameIndex++,
                    SourceFileIndex = sourceIndices.GetValueOrDefault(ordered[cursor.SourceIndex].Path, -1),
                    SourceFrameIndex = cursor.Frame.FrameIndex
                };
                merged.Append(frame);
                coverage.Add(frame);
                if (!cursor.Enumerator.MoveNext()) continue;
                var next = cursor with { Frame = cursor.Enumerator.Current };
                queue.Enqueue(next, (checked(next.Frame.TimestampNanoseconds + next.Offset), next.SourceIndex, next.Frame.FrameIndex));
            }

            merged.Complete();
            var totalLines = parsedFiles.Sum(static file => file.Result.Report.TotalLines);
            var nonDataLines = parsedFiles.Sum(static file => file.Result.Report.NonDataLines);
            var acceptedLines = parsedFiles.Sum(static file => file.Result.Report.AcceptedLines);
            var rejectedLines = parsedFiles.Sum(static file => file.Result.Report.RejectedLines);
            var report = coverage.Apply(new ImportReport(Name, totalLines, nonDataLines, acceptedLines, rejectedLines, issues, mode));
            report = ApplyOmissions(report, parsedFiles, sourceIndices, sources, omissions, earliest);
            var completeness = omissions.Count > 0 || parsedFiles.Any(static file => file.Result.Completeness == DatasetCompleteness.Partial) || report.HasErrors
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

    private static ImportReport ApplyOmissions(ImportReport report,
        IReadOnlyList<(string Path, CanLogParseResult Result)> parsedFiles,
        IReadOnlyDictionary<string, int> sourceIndices, IReadOnlyList<SourceLogFile> sources,
        IReadOnlyList<OnlineLogArchiveOmission> omissions, DateTimeOffset earliest)
    {
        if (omissions.Count == 0) return report;
        if (sourceIndices.Count != sources.Count || parsedFiles.Count != sources.Count)
            throw new InvalidDataException("De converter kan niet ieder opgenomen bestand eenduidig aan een bron koppelen; uitsluitingsgrenzen zijn niet bewijsbaar.");

        var ranges = parsedFiles.ToDictionary(file => sourceIndices[file.Path], file =>
        {
            var offset = file.Result.StartTimeUtc is { } origin ? checked((origin - earliest).Ticks * 100L) : 0L;
            if (file.Result.Frames.Count == 0)
                throw new InvalidDataException("Een leeg opgenomen deelbestand kan geen uitsluitingsgrens bewijzen.");
            return (Start: checked(offset + file.Result.Frames[0].TimestampNanoseconds),
                End: checked(offset + file.Result.Frames[^1].TimestampNanoseconds));
        });
        var sequence = sources.Select((source, index) => new ArchiveSequenceItem(source.Session!, source.Name, index, null))
            .Concat(omissions.Select(file => new ArchiveSequenceItem(file.Session,
                SourceLogFile.FromArchiveEntry(file.ArchiveName).Name, -1, file)))
            .OrderBy(file => uint.Parse(file.Session, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ThenBy(file => OnlineLogSequencePolicy.TryParsePartNumber(file.Name, out var part) ? part : int.MaxValue).ToArray();
        var included = sequence.Where(file => file.SourceIndex >= 0).Select(file => file.SourceIndex).ToArray();
        var before = new int[included.Length];
        var after = new int[included.Length];
        for (var i = 0; i < included.Length; i++)
            before[i] = i == 0 || ranges[included[i]].End > ranges[before[i - 1]].End ? included[i] : before[i - 1];
        for (var i = included.Length - 1; i >= 0; i--)
            after[i] = i == included.Length - 1 || ranges[included[i]].Start < ranges[after[i + 1]].Start ? included[i] : after[i + 1];

        var boundaries = new List<MeasurementContinuityBreak>();
        var pending = new List<string>();
        var ordinal = 0;
        foreach (var item in sequence)
        {
            if (item.Omission is { } omission)
            {
                if (ordinal > 0) pending.Add(omission.ObjectKey);
                continue;
            }
            if (ordinal > 0 && pending.Count > 0)
            {
                var left = before[ordinal - 1];
                var right = after[ordinal];
                var start = ranges[left].End;
                var end = ranges[right].Start;
                // A fabricated epsilon would corrupt durations. Overlapping, equal or
                // reversed clocks therefore require separately opened segments.
                if (end <= start || end / 1e9 <= start / 1e9)
                    throw new InvalidDataException("Opgenomen delen rond een uitsluiting overlappen of hebben geen bewijsbare positieve tijdgrens. Open de segmenten afzonderlijk.");
                boundaries.Add(new(left, right, start, end, pending.ToArray()));
            }
            pending.Clear();
            ordinal++;
        }
        var boundaryGaps = boundaries.Select(boundary => new MeasurementGap(
            boundary.BeforeTimestampNanoseconds / 1e9, boundary.AfterTimestampNanoseconds / 1e9)).ToArray();
        return report with
        {
            Exclusions = omissions.ToArray(),
            ContinuityBreaks = boundaries,
            Gaps = MeasurementGapSelection.Union(report.Gaps.Concat(boundaryGaps)),
            GapsByChannel = report.GapsByChannel.ToDictionary(pair => pair.Key,
                pair => MeasurementGapSelection.Union(pair.Value.Concat(boundaryGaps)), StringComparer.OrdinalIgnoreCase),
            Issues = report.Issues.Append(new ImportIssue(ImportIssueSeverity.Warning, "EXPLICIT_EXCLUSIONS", "CANedge MDF4", 0,
                $"{omissions.Count} bestand(en) bewust uitgesloten; {boundaries.Count} grens/grenzen tussen opgenomen segmenten. Uitsluitingen tellen niet als gemeten dekking.", "")).ToArray()
        };
    }

    private sealed record ArchiveSequenceItem(string Session, string Name, int SourceIndex, OnlineLogArchiveOmission? Omission);

    private static string SanitizeFileStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrEmpty(result) ? "log" : result;
    }

    private sealed record MergeCursor(
        int SourceIndex,
        long Offset,
        IEnumerator<RawCanFrame> Enumerator,
        RawCanFrame Frame);
}
