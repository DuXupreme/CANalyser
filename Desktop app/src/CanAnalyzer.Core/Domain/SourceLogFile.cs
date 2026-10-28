namespace CanAnalyzer.Core.Domain;

/// <summary>Original source identity, retained independently of temporary conversion/cache filenames.</summary>
public sealed record SourceLogFile(string Name, string SourcePath, string? Logger, string? Session)
{
    /// <summary>First measurement according to the logger clock; not independently verified UTC.</summary>
    public DateTimeOffset? RecordedAt { get; init; }

    public DateTimeOffset? RawRecordedAtUtc { get => RecordedAt; init => RecordedAt = value; }
    public DateTimeOffset? UploadedAtUtc { get; init; }
    public DateTimeOffset? LoggerStartTimeUtc { get; init; }
    public string ClockQuality { get; init; } = "unverified";
    public string? ClockEvidence { get; init; }
    public string? Sha256 { get; init; }
    public long? SizeBytes { get; init; }
    public string? ObjectKey { get; init; }
    public string? ObjectVersion { get; init; }
    public string? MetadataRevision { get; init; }
    public string? MachineId { get; init; }
    public string? InstallationId { get; init; }
    public string? ConfigurationId { get; init; }
    public string MachineAssignmentQuality { get; init; } = "unknown";
    public string? MachineAssignmentEvidence { get; init; }
    public string? ReportedMachineName { get; init; }
    public string? DeviceMetadata { get; init; }
    public string? ConfigurationCrc32 { get; init; }
    public string? FirmwareVersion { get; init; }

    public static SourceLogFile FromArchiveEntry(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return new(parts.LastOrDefault() ?? entryPath, normalized,
            parts.Length >= 3 ? parts[^3] : null,
            parts.Length >= 3 ? parts[^2] : null);
    }

    public static SourceLogFile FromLocalFile(string path)
    {
        var source = FromArchiveEntry(path);
        // Only infer logger/session from a recognizable CANedge directory layout.
        var recognizable = Path.GetExtension(path).Equals(".mf4", StringComparison.OrdinalIgnoreCase) &&
                           source.Logger is { Length: 8 } logger && logger.All(Uri.IsHexDigit);
        return recognizable ? source : source with { Logger = null, Session = null };
    }
}
