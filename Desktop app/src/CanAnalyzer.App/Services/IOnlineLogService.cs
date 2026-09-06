namespace CanAnalyzer.App.Services;

public sealed record OnlineLogFile(
    string Key,
    string Name,
    string Machine,
    string Logger,
    string Session,
    DateTimeOffset CreatedAt,
    long SizeBytes);

public sealed record OnlineLogQueryResult(
    IReadOnlyList<OnlineLogFile> Files,
    bool Truncated,
    int MaximumSelection);

public sealed record OnlineDownloadProgress(long BytesReceived, long? TotalBytes);

public sealed record OnlineLogSelection(
    string Key,
    string Name,
    string Logger,
    string Session,
    long SizeBytes);

public interface IOnlineLogService
{
    Task<OnlineLogQueryResult> GetLogsAsync(
        string loggerId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);

    Task<string> DownloadArchiveAsync(
        IReadOnlyList<OnlineLogSelection> files,
        IProgress<OnlineDownloadProgress>? progress,
        CancellationToken cancellationToken);
}
