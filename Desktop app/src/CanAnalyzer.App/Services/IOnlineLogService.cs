namespace CanAnalyzer.App.Services;

public sealed record OnlineLogFile(
    string Key,
    string Name,
    string Machine,
    string Logger,
    string Session,
    DateTimeOffset? RecordedAt,
    DateTimeOffset UploadedAt,
    long SizeBytes)
{
    public DateTimeOffset? RawRecordedAtUtc => RecordedAt;
    public string ClockQuality { get; init; } = "unverified";
    public string? ClockEvidence { get; init; }
    public string MachineAssignmentQuality { get; init; } = "unverified";
    public string? MachineAssignmentEvidence { get; init; }
    public string? MachineId { get; init; }
    public string? InstallationId { get; init; }
    public string? ConfigurationId { get; init; }
    public string? ObjectVersion { get; init; }
    public string? ETag { get; init; }
    public string? MetadataRevision { get; init; }
    public string? Sha256 { get; init; }
    public OnlineLogAnnotation? Annotation { get; init; }
    public bool RequiresAnnotationCheck { get; init; }

    public OnlineLogSelection ToSelection() => new(Key, Name, Logger, Session, SizeBytes)
    {
        RawRecordedAtUtc = RecordedAt,
        UploadedAtUtc = UploadedAt == default ? null : UploadedAt,
        ClockQuality = ClockQuality, ClockEvidence = ClockEvidence,
        MachineName = Machine, MachineAssignmentQuality = MachineAssignmentQuality,
        MachineAssignmentEvidence = MachineAssignmentEvidence,
        MachineId = MachineId, InstallationId = InstallationId, ConfigurationId = ConfigurationId,
        ObjectVersion = ObjectVersion, ETag = ETag, MetadataRevision = MetadataRevision, Sha256 = Sha256,
        Annotation = Annotation, RequiresAnnotationCheck = RequiresAnnotationCheck
    };
}

public sealed record OnlineLogQueryResult(
    IReadOnlyList<OnlineLogFile> Files,
    bool Truncated,
    int MaximumSelection,
    int UnknownRecordingTimes)
{
    public bool AnnotationsAvailable { get; init; }
    public int DownloadManifestVersion { get; init; }
}

public sealed record OnlineLogAnnotation(bool Excluded, string Reason, string? Revision,
    DateTimeOffset? UpdatedAt = null, string? Actor = null);
public sealed record OnlineLogAnnotationUpdate(string Key, string ExpectedObjectVersion,
    string? ExpectedAnnotationRevision, bool Excluded, string Reason);
public sealed record OnlineLogAnnotationResult(string Key, string ObjectVersion, bool Excluded,
    string Reason, string Revision, DateTimeOffset? UpdatedAt, string? Actor);

public sealed record OnlineDownloadProgress(long BytesReceived, long? TotalBytes);

public sealed record OnlineLogSelection(
    string Key,
    string Name,
    string Logger,
    string Session,
    long SizeBytes)
{
    public DateTimeOffset? RawRecordedAtUtc { get; init; }
    public DateTimeOffset? UploadedAtUtc { get; init; }
    public string ClockQuality { get; init; } = "unverified";
    public string? ClockEvidence { get; init; }
    public string? MachineName { get; init; }
    public string MachineAssignmentQuality { get; init; } = "unverified";
    public string? MachineAssignmentEvidence { get; init; }
    public string? MachineId { get; init; }
    public string? InstallationId { get; init; }
    public string? ConfigurationId { get; init; }
    public string? ObjectVersion { get; init; }
    public string? ETag { get; init; }
    public string? MetadataRevision { get; init; }
    public string? Sha256 { get; init; }
    public OnlineLogAnnotation? Annotation { get; init; }
    public bool RequiresAnnotationCheck { get; init; }
}

public interface IOnlineLogService
{
    string? AuthenticationUserName { get; }
    bool RememberCredentials { get; }

    void SetCredentials(string username, string password, bool rememberFor30Days = false);

    void ClearCredentials();

    Task<OnlineLogQueryResult> GetLogsAsync(
        string machineId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);

    Task<OnlineLogQueryResult> GetLogsAsync(string machineId, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, string timeBasis, CancellationToken cancellationToken) =>
        timeBasis == "recording" ? GetLogsAsync(machineId, fromUtc, toUtc, cancellationToken)
            : throw new NotSupportedException("Deze bron ondersteunt zoeken op ontvangstdatum nog niet.");

    Task<IReadOnlyList<OnlineLogAnnotationResult>> UpdateAnnotationsAsync(
        IReadOnlyList<OnlineLogAnnotationUpdate> updates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Gedeelde markeringen zijn op dit dashboard nog niet beschikbaar.");

    Task<string> DownloadArchiveAsync(
        IReadOnlyList<OnlineLogSelection> files,
        IProgress<OnlineDownloadProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class OnlineLogAuthenticationException : System.Net.Http.HttpRequestException
{
    public OnlineLogAuthenticationException(bool credentialsRejected)
        : base(credentialsRejected
            ? "Het dashboard heeft de inloggegevens niet geaccepteerd. Controleer je gebruikersnaam en wachtwoord."
            : "Log in op het dashboard om online bestanden te openen.",
            null, System.Net.HttpStatusCode.Unauthorized)
    {
        CredentialsRejected = credentialsRejected;
    }

    public bool CredentialsRejected { get; }
}
