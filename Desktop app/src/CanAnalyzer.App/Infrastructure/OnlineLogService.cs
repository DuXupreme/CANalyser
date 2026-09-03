using System.Globalization;
using System.IO;
using System.IO.Compression;
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
        IReadOnlyList<OnlineLogSelection> files,
        IProgress<OnlineDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0) throw new ArgumentException("Selecteer minimaal één logbestand.", nameof(files));
        var keys = files.Select(static file => file.Key).ToArray();
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

        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CANalyser", "online-cache");
        Directory.CreateDirectory(cacheDirectory);
        CleanupOldArchives(cacheDirectory);
        var finalPath = Path.Combine(cacheDirectory, $"online-logs-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var partialPath = finalPath + ".partial";
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
