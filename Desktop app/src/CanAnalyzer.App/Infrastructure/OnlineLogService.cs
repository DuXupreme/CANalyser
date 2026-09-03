using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CanAnalyzer.App.Services;

namespace CanAnalyzer.App.Infrastructure;

/// <summary>Reads the public dashboard API; AWS credentials remain exclusively on the server.</summary>
public sealed class OnlineLogService : IOnlineLogService, IDisposable
{
    internal const string DashboardBaseUrl = "https://main.d2qydggp5q6c4q.amplifyapp.com/";
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri(DashboardBaseUrl), Timeout = TimeSpan.FromMinutes(15) };

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
        IReadOnlyList<string> keys,
        IProgress<OnlineDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0) throw new ArgumentException("Selecteer minimaal één logbestand.", nameof(keys));
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CANalyser", "online-cache");
        Directory.CreateDirectory(cacheDirectory);
        CleanupOldArchives(cacheDirectory);
        var finalPath = Path.Combine(cacheDirectory, $"online-logs-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var partialPath = finalPath + ".partial";
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("api/download-selected", new { keys }, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long received = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(new OnlineDownloadProgress(received, total));
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            File.Move(partialPath, finalPath);
            return finalPath;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    public void Dispose() => _httpClient.Dispose();

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

    private static void CleanupOldArchives(string directory)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var path in Directory.EnumerateFiles(directory, "online-logs-*.zip*", SearchOption.TopDirectoryOnly))
        {
            try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record LogListResponse(LogFileResponse[] Files, bool Truncated, int MaximumSelection);
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
