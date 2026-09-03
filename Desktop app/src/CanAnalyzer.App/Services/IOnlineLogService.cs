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

public interface IOnlineLogService
{
    Task<OnlineLogQueryResult> GetLogsAsync(
        string loggerId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);

    Task<string> DownloadArchiveAsync(
        IReadOnlyList<string> keys,
        IProgress<OnlineDownloadProgress>? progress,
        CancellationToken cancellationToken);
}
