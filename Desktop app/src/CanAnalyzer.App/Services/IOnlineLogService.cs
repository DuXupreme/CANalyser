namespace CanAnalyzer.App.Services;

public sealed record OnlineLogFile(
    string Key,
    string Name,
    string Machine,
    string Logger,
    string Session,
    DateTimeOffset? RecordedAt,
    DateTimeOffset UploadedAt,
    long SizeBytes);

public sealed record OnlineLogQueryResult(
    IReadOnlyList<OnlineLogFile> Files,
    bool Truncated,
    int MaximumSelection,
    int UnknownRecordingTimes);

public sealed record OnlineDownloadProgress(long BytesReceived, long? TotalBytes);

public sealed record OnlineLogSelection(
    string Key,
    string Name,
    string Logger,
    string Session,
    long SizeBytes);

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
