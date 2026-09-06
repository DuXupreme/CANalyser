using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Infrastructure;

/// <summary>Reads the public dashboard API; AWS credentials remain exclusively on the server.</summary>
public sealed class OnlineLogService : IOnlineLogService, IDisposable
{
    internal const string DashboardBaseUrl = "https://main.d2qydggp5q6c4q.amplifyapp.com/";
    private const long MaximumCacheBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan CacheRetention = TimeSpan.FromDays(7);
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri(DashboardBaseUrl), Timeout = TimeSpan.FromMinutes(15) };

    public OnlineLogService()
    {
        var cacheDirectory = GetCacheDirectory();
        try
        {
            if (Directory.Exists(cacheDirectory)) CleanupCache(cacheDirectory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task<OnlineLogQueryResult> GetLogsAsync(
        string loggerId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var url = "api/logs?machine=" + Uri.EscapeDataString(loggerId)
                  + "&from=" + Uri.EscapeDataString(fromUtc.ToString("O", CultureInfo.InvariantCulture))
                  + "&to=" + Uri.EscapeDataString(toUtc.ToString("O", CultureInfo.InvariantCulture));
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<LogListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidDataException("De online-loglijst is leeg of ongeldig.");
        return new OnlineLogQueryResult(
            payload.Files.Select(static file => new OnlineLogFile(
                file.Key,
                file.Name,
                file.Machine,
                file.Logger,
                file.Session,
                file.CreatedAt,
                file.SizeBytes)).ToArray(),
            payload.Truncated,
            payload.MaximumSelection);
    }

    public async Task<string> DownloadArchiveAsync(
        IReadOnlyList<OnlineLogSelection> files,
        IProgress<OnlineDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0) throw new ArgumentException("Selecteer minimaal één logbestand.", nameof(files));
        var selectionValidation = OnlineLogSequencePolicy.Validate(files
            .Select(static file => new OnlineLogPartIdentity(file.Logger, file.Session, file.Name))
            .ToArray());
        if (!selectionValidation.IsValid) throw new InvalidOperationException(selectionValidation.Message);
        var keys = files.Select(static file => file.Key).ToArray();

        var cacheDirectory = GetCacheDirectory();
        Directory.CreateDirectory(cacheDirectory);
        CleanupCache(cacheDirectory);
        var finalPath = BuildCachePath(cacheDirectory, files);
        if (IsUsableCachedArchive(finalPath, files))
        {
            File.SetLastWriteTimeUtc(finalPath, DateTime.UtcNow);
            var cachedBytes = files.Sum(static file => Math.Max(0, file.SizeBytes));
            progress?.Report(new OnlineDownloadProgress(cachedBytes, cachedBytes));
            return finalPath;
        }

        TryDelete(finalPath);
        using var planResponse = await _httpClient.PostAsJsonAsync("api/download-plan", new { keys }, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(planResponse, cancellationToken).ConfigureAwait(false);
        var plan = await planResponse.Content.ReadFromJsonAsync<DownloadPlanResponse>(cancellationToken: cancellationToken)
                       .ConfigureAwait(false)
                   ?? throw new InvalidDataException("Het online-downloadplan is leeg of ongeldig.");
        var plannedFiles = plan.Files
                           ?? throw new InvalidDataException("Het online-downloadplan bevat geen bestanden.");
        if (plannedFiles.Length != files.Count)
            throw new InvalidDataException("Het online-downloadplan bevat niet alle geselecteerde bestanden.");

        var partialPath = finalPath + ".partial";
        TryDelete(partialPath);
        try
        {
            var total = files.Sum(static file => Math.Max(0, file.SizeBytes));
            long received = 0;
            await using (var target = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (var index = 0; index < plannedFiles.Length; index++)
                    {
                        var plannedFile = plannedFiles[index];
                        var downloadUri = ValidateDownloadUri(plannedFile.Url);
                        var archiveName = ValidateArchiveName(plannedFile.ArchiveName);
                        using var response = await _httpClient.GetAsync(
                                downloadUri,
                                HttpCompletionOption.ResponseHeadersRead,
                                cancellationToken)
                            .ConfigureAwait(false);
                        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                            .ConfigureAwait(false);
                        var entry = archive.CreateEntry(archiveName, CompressionLevel.Optimal);
                        await using var destination = entry.Open();
                        var buffer = new byte[128 * 1024];
                        while (true)
                        {
                            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (read == 0) break;
                            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            received += read;
                            progress?.Report(new OnlineDownloadProgress(received, total > 0 ? total : null));
                        }
                    }
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(partialPath, finalPath);
            CleanupCache(cacheDirectory, finalPath);
            return finalPath;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static Uri ValidateDownloadUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Het online-downloadplan bevat een ongeldige downloadlink.");
        return uri;
    }

    private static string ValidateArchiveName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Het online-downloadplan bevat een ongeldige bestandsnaam.");
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException("Het online-downloadplan bevat een ongeldige bestandsnaam.");
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new InvalidDataException("Het online-downloadplan bevat een ongeldige bestandsnaam.");
        return string.Join('/', segments);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var error = JsonSerializer.Deserialize<ErrorResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (!string.IsNullOrWhiteSpace(error?.Error))
                throw new HttpRequestException(error.Error, null, response.StatusCode);
        }
        catch (JsonException)
        {
            // Fall through to the generic HTTP message.
        }

        throw new HttpRequestException($"Online logs ophalen is mislukt (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
    }

    private static string BuildCachePath(string directory, IReadOnlyList<OnlineLogSelection> files)
    {
        var identity = string.Join('\n', files
            .OrderBy(static file => file.Key, StringComparer.Ordinal)
            .Select(static file => $"{file.Key}\t{file.SizeBytes.ToString(CultureInfo.InvariantCulture)}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return Path.Combine(directory, $"online-logs-{hash}.zip");
    }

    private static string GetCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CANalyser", "online-cache");

    private static bool IsUsableCachedArchive(string path, IReadOnlyList<OnlineLogSelection> files)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count != files.Count) return false;
            var expectedLengths = files.Select(static file => file.SizeBytes).Order().ToArray();
            var actualLengths = archive.Entries.Select(static entry => entry.Length).Order().ToArray();
            return expectedLengths.SequenceEqual(actualLengths);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void CleanupCache(string directory, string? preservePath = null)
    {
        var cutoff = DateTime.UtcNow - CacheRetention;
        var candidates = Directory
            .EnumerateFiles(directory, "online-logs-*", SearchOption.TopDirectoryOnly)
            .Select(static path => new FileInfo(path))
            .ToList();

        foreach (var file in candidates.Where(file =>
                     !string.Equals(file.FullName, preservePath, StringComparison.OrdinalIgnoreCase) &&
                     file.LastWriteTimeUtc < cutoff))
        {
            TryDelete(file.FullName);
        }

        var archives = Directory
            .EnumerateFiles(directory, "online-logs-*.zip", SearchOption.TopDirectoryOnly)
            .Select(static path => new FileInfo(path))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ToList();
        var totalBytes = archives.Sum(static file => file.Length);
        foreach (var file in archives.AsEnumerable().Reverse())
        {
            if (totalBytes <= MaximumCacheBytes) break;
            if (string.Equals(file.FullName, preservePath, StringComparison.OrdinalIgnoreCase)) continue;
            var length = file.Length;
            TryDelete(file.FullName);
            if (!File.Exists(file.FullName)) totalBytes -= length;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record LogListResponse(LogFileResponse[] Files, bool Truncated, int MaximumSelection);
    private sealed record DownloadPlanResponse(DownloadFileResponse[]? Files, DateTimeOffset ExpiresAt);
    private sealed record DownloadFileResponse(string? ArchiveName, string? Url);
    private sealed record LogFileResponse(
        string Key,
        string Name,
        string Machine,
        string Logger,
        string Session,
        DateTimeOffset CreatedAt,
        long SizeBytes);
    private sealed record ErrorResponse(string Error);
}
