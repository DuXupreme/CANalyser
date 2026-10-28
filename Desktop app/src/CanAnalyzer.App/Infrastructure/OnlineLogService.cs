using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Infrastructure;

/// <summary>Reads the dashboard API; AWS credentials remain exclusively on the server.</summary>
public sealed partial class OnlineLogService : IOnlineLogService, IDisposable
{
    internal const string DashboardBaseUrl = "https://main.d2qydggp5q6c4q.amplifyapp.com/";
    private const long MaximumCacheBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan CacheRetention = TimeSpan.FromDays(7);
    private readonly string? _cacheDirectoryOverride;
    private readonly HttpClient _httpClient;
    private readonly IOnlineLogCredentialStore? _credentialStore;
    private readonly TimeProvider _timeProvider;
    private DashboardCredentials? _credentials;

    public string? AuthenticationUserName => GetCurrentCredentials()?.UserName;
    public bool RememberCredentials => GetCurrentCredentials()?.Remembered is not null;

    public void SetCredentials(string username, string password, bool rememberFor30Days = false)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Vul een gebruikersnaam in.", nameof(username));
        if (username.Contains(':'))
            throw new ArgumentException("De gebruikersnaam mag geen dubbele punt bevatten.", nameof(username));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Vul een wachtwoord in.", nameof(password));
        if (rememberFor30Days && _credentialStore is null)
            throw new InvalidOperationException("Inloggegevens onthouden is hier niet beschikbaar.");
        // Remove a previously remembered account even if this login is session-only or rejected.
        _credentialStore?.Clear();
        var parameter = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        var now = _timeProvider.GetUtcNow();
        _credentials = new DashboardCredentials(username, new AuthenticationHeaderValue("Basic", parameter))
        {
            Remembered = rememberFor30Days ? new RememberedOnlineLogCredentials
            {
                DashboardUrl = _httpClient.BaseAddress!.AbsoluteUri,
                UserName = username,
                AuthorizationParameter = parameter,
                SavedAtUtc = now,
                ExpiresAtUtc = now.AddDays(RememberedOnlineLogCredentials.MaximumDays)
            } : null,
            NeedsSave = rememberFor30Days
        };
    }

    public void ClearCredentials()
    {
        // A failed removal is reported to the user; do not claim sign-out succeeded.
        _credentialStore?.Clear();
        _credentials = null;
    }

    public OnlineLogService(IOnlineLogCredentialStore credentialStore)
        : this(new HttpClient { BaseAddress = new Uri(DashboardBaseUrl), Timeout = TimeSpan.FromMinutes(15) },
            credentialStore: credentialStore)
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
        string machineId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
        => await GetLogsAsync(machineId, fromUtc, toUtc, "recording", cancellationToken).ConfigureAwait(false);

    public async Task<OnlineLogQueryResult> GetLogsAsync(string machineId, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, string timeBasis, CancellationToken cancellationToken)
    {
        if (timeBasis is not ("recording" or "uploaded")) throw new ArgumentOutOfRangeException(nameof(timeBasis));
        var result = await GetLogPageAsync(machineId, fromUtc, toUtc, timeBasis, cancellationToken).ConfigureAwait(false);
        if (!result.Truncated) return result;
        // The API caps each response at 200 newest files. Split the half-open
        // recording interval so older morning logs cannot silently disappear.
        if (toUtc - fromUtc <= TimeSpan.FromMilliseconds(1))
            throw new InvalidDataException("Te veel logs met dezelfde meettijd; de loglijst kan niet volledig worden geladen.");
        var middle = fromUtc.AddTicks((toUtc - fromUtc).Ticks / 2);
        var earlier = await GetLogsAsync(machineId, fromUtc, middle, timeBasis, cancellationToken).ConfigureAwait(false);
        var later = await GetLogsAsync(machineId, middle, toUtc, timeBasis, cancellationToken).ConfigureAwait(false);
        return new OnlineLogQueryResult(earlier.Files.Concat(later.Files)
            .DistinctBy(static file => file.Key)
            .OrderByDescending(file => timeBasis == "uploaded" ? file.UploadedAt : file.RecordedAt).ToArray(), false,
            Math.Min(earlier.MaximumSelection, later.MaximumSelection),
            Math.Max(earlier.UnknownRecordingTimes, later.UnknownRecordingTimes))
        { AnnotationsAvailable = earlier.AnnotationsAvailable && later.AnnotationsAvailable,
            DownloadManifestVersion = Math.Min(earlier.DownloadManifestVersion, later.DownloadManifestVersion) };
    }

    internal OnlineLogService(HttpClient httpClient, string? cacheDirectory = null,
        IOnlineLogCredentialStore? credentialStore = null, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _cacheDirectoryOverride = cacheDirectory;
        _credentialStore = credentialStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var saved = credentialStore?.Load();
        if (saved?.IsValidAt(_timeProvider.GetUtcNow(), httpClient.BaseAddress!.AbsoluteUri) == true)
            _credentials = new DashboardCredentials(saved.UserName, new AuthenticationHeaderValue("Basic", saved.AuthorizationParameter))
            { Remembered = saved };
    }

    private async Task<OnlineLogQueryResult> GetLogPageAsync(
        string machineId, DateTimeOffset fromUtc, DateTimeOffset toUtc, string timeBasis, CancellationToken cancellationToken)
    {
        var url = "api/logs?machine=" + Uri.EscapeDataString(machineId)
                  + "&from=" + Uri.EscapeDataString(fromUtc.ToString("O", CultureInfo.InvariantCulture))
                  + "&to=" + Uri.EscapeDataString(toUtc.ToString("O", CultureInfo.InvariantCulture))
                  + "&timeBasis=" + timeBasis + "&includeExcluded=true";
        using var request = CreateDashboardRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureDashboardSuccessAsync(request, response, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<LogListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidDataException("De online-loglijst is leeg of ongeldig.");
        if (!string.Equals(payload.TimeBasis, timeBasis, StringComparison.Ordinal))
            throw new InvalidDataException("Het dashboard ondersteunt selectie op meettijd nog niet. Werk eerst het dashboard bij; uploadtijden worden niet als meettijden gebruikt.");
        return new OnlineLogQueryResult(
            payload.Files.Select(file => new OnlineLogFile(
                file.Key,
                file.Name,
                file.Machine,
                file.Logger,
                file.Session,
                file.RecordedAt,
                file.UploadedAt ?? file.CreatedAt,
                file.SizeBytes)
            {
                ClockQuality = NormalizeClockQuality(file.ClockQuality, file.ClockEvidence, file.RecordedAt),
                ClockEvidence = file.ClockEvidence,
                MachineAssignmentQuality = NormalizeMachineQuality(file.MachineAssignmentQuality, file.MachineAssignmentEvidence, file.MachineId, file.InstallationId),
                MachineAssignmentEvidence = file.MachineAssignmentEvidence,
                MachineId = file.MachineId,
                InstallationId = file.InstallationId,
                ConfigurationId = file.ConfigurationId,
                ObjectVersion = file.ObjectVersion ?? file.ETag,
                ETag = file.ETag,
                MetadataRevision = file.MetadataRevision,
                Sha256 = file.Sha256,
                Annotation = payload.Capabilities?.Annotations?.Available == true ? file.Annotation : null,
                RequiresAnnotationCheck = payload.Capabilities?.Annotations?.Version >= 1
            }).ToArray(),
            payload.Truncated,
            Mdf4ImportLimits.MaximumFiles,
            payload.UnknownRecordingTimes)
        { AnnotationsAvailable = payload.Capabilities?.Annotations?.Available == true,
            DownloadManifestVersion = payload.Capabilities?.DownloadManifestVersion ?? 1 };
    }

    public async Task<string> DownloadArchiveAsync(
        IReadOnlyList<OnlineLogSelection> files,
        IProgress<OnlineDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0) throw new ArgumentException("Selecteer minimaal één logbestand.", nameof(files));
        if (files.Count > Mdf4ImportLimits.MaximumFiles ||
            files.Any(static file => file.SizeBytes < 0 || file.SizeBytes > Mdf4ImportLimits.MaximumFileBytes) ||
            files.Sum(static file => file.SizeBytes) > Mdf4ImportLimits.MaximumBytes)
            throw new InvalidDataException("De selectie overschrijdt de importgrens van 4 GB, 512 MB per bestand of 10.000 bestanden. Kies een kleinere periode.");
        var selectionValidation = OnlineLogSequencePolicy.Validate(files
            .Select(static file => new OnlineLogPartIdentity(file.Logger, file.Session, file.Name))
            .ToArray());
        if (!selectionValidation.IsValid) throw new InvalidOperationException(selectionValidation.Message);
        var keys = files.Select(static file => file.Key).ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new InvalidDataException("De selectie bevat hetzelfde logbestand meer dan één keer.");
        var cacheSelection = files;
        var annotated = files.Any(file => file.RequiresAnnotationCheck || file.Annotation is not null)
            ? await PrepareAnnotatedDownloadAsync(files, cancellationToken).ConfigureAwait(false) : null;
        if (annotated is not null)
        {
            files = files.Where(file => annotated.Downloads.ContainsKey(file.Key)).Select(file => file with
            {
                Sha256 = annotated.Downloads[file.Key].Sha256 ?? file.Sha256,
                MetadataRevision = annotated.Downloads[file.Key].MetadataRevision ?? file.MetadataRevision,
                ETag = annotated.Downloads[file.Key].ETag ?? file.ETag
            }).ToArray();
            keys = files.Select(file => file.Key).ToArray();
        }
        if (files.Count == 0) throw new InvalidDataException("Alle geselecteerde bestanden zijn als niet nuttig gemarkeerd. Verwijder zo nodig eerst een markering.");
        var byKey = files.ToDictionary(static file => file.Key, StringComparer.Ordinal);

        var cacheDirectory = GetCacheDirectory();
        Directory.CreateDirectory(cacheDirectory);
        CleanupCache(cacheDirectory);
        var finalPath = BuildCachePath(cacheDirectory, cacheSelection);
        if (await IsUsableCachedArchiveAsync(finalPath, files, cancellationToken, annotated?.Omissions).ConfigureAwait(false))
        {
            File.SetLastWriteTimeUtc(finalPath, DateTime.UtcNow);
            var cachedBytes = files.Sum(static file => Math.Max(0, file.SizeBytes));
            progress?.Report(new OnlineDownloadProgress(cachedBytes, cachedBytes));
            return finalPath;
        }

        TryDelete(finalPath);
        var partialPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            var total = files.Sum(static file => Math.Max(0, file.SizeBytes));
            long received = 0;
            await using (var target = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifestFiles = new List<OnlineLogArchiveFile>();
                    var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var downloadedKeys = new HashSet<string>(StringComparer.Ordinal);
                    // Request at most 200 signed URLs at a time, but put every
                    // batch into the same archive for one complete analysis.
                    foreach (var batch in keys.Chunk(200))
                    {
                        // Renew URLs just before each batch; an earlier cache-validation
                        // plan must not expire while a large selection downloads.
                        var plan = await RequestDownloadPlanAsync(batch, cancellationToken,
                            annotated is null ? null : batch.Select(key => byKey[key]).ToArray()).ConfigureAwait(false);
                        if (plan.Files is not { } plannedFiles || plannedFiles.Length != batch.Length)
                            throw new InvalidDataException("Het online-downloadplan bevat niet alle geselecteerde bestanden.");
                        foreach (var plannedFile in plannedFiles)
                        {
                            var downloadUri = ValidateDownloadUri(plannedFile.Url);
                            var archiveName = ValidateArchiveName(plannedFile.ArchiveName);
                            // Older dashboards identify the object by its unchanged path in the ZIP.
                            // Never pair a reordered/duplicate plan to selections by array index.
                            var key = plannedFile.Key ?? archiveName;
                            if (!batch.Contains(key, StringComparer.Ordinal) || !byKey.TryGetValue(key, out var selected) ||
                                !downloadedKeys.Add(key) || !archiveNames.Add(archiveName))
                                throw new InvalidDataException("Het online-downloadplan bevat onverwachte of dubbele bestanden. Vernieuw de loglijst.");
                            if (plannedFile.SizeBytes is { } plannedSize && plannedSize != selected.SizeBytes)
                                throw new InvalidDataException("Een online logbestand is gewijzigd. Vernieuw de loglijst.");
                            if (annotated is not null && (plan.SchemaVersion != 2 ||
                                plannedFile.AnnotationRevision != selected.Annotation?.Revision ||
                                plannedFile.ObjectVersion != selected.ObjectVersion))
                                throw new InvalidDataException("Een bestand of markering is gewijzigd. Vernieuw de loglijst.");
                            if (plannedFile.ObjectVersion is { } plannedVersion && selected.ObjectVersion is { } selectedVersion &&
                                !string.Equals(plannedVersion, selectedVersion, StringComparison.Ordinal))
                                throw new InvalidDataException("Een online logversie is gewijzigd. Vernieuw de loglijst.");
                            if (plannedFile.MetadataRevision is { } plannedRevision && selected.MetadataRevision is { } selectedRevision &&
                                !string.Equals(plannedRevision, selectedRevision, StringComparison.Ordinal))
                                throw new InvalidDataException("De online bronmetadata is gewijzigd. Vernieuw de loglijst.");
                            var expectedETag = plannedFile.ETag ?? selected.ETag;
                            if (plannedFile.ETag is { } plannedETag && selected.ETag is { } selectedETag && plannedETag != selectedETag)
                                throw new InvalidDataException("Een online logversie is gewijzigd. Vernieuw de loglijst.");
                            using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUri);
                            if (expectedETag is not null)
                                downloadRequest.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(expectedETag));
                            else if (selected.UploadedAtUtc is { } observedUpload)
                                downloadRequest.Headers.IfUnmodifiedSince = observedUpload;
                            using var response = await _httpClient.SendAsync(
                                    downloadRequest,
                                    HttpCompletionOption.ResponseHeadersRead,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                            if (expectedETag is not null && response.Headers.ETag?.ToString() != expectedETag)
                                throw new InvalidDataException("De ontvangen logversie wijkt af van het downloadplan (ETag).");
                            if (plannedFile.VersionId is { Length: > 0 } versionId &&
                                (!response.Headers.TryGetValues("x-amz-version-id", out var versions) || versions.SingleOrDefault() != versionId))
                                throw new InvalidDataException("De ontvangen logversie wijkt af van het downloadplan (version ID).");
                            if (selected.UploadedAtUtc is { } selectedUpload && response.Content.Headers.LastModified is { } receivedUpload &&
                                selectedUpload.ToUnixTimeSeconds() != receivedUpload.ToUnixTimeSeconds())
                                throw new InvalidDataException("De ontvangen uploadtijd wijkt af van de loglijst. Vernieuw de loglijst.");
                            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != selected.SizeBytes)
                                throw new InvalidDataException("De downloadgrootte wijkt af van de loglijst. Vernieuw de loglijst.");
                            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                                .ConfigureAwait(false);
                            var entry = archive.CreateEntry(archiveName, CompressionLevel.Optimal);
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            long entryBytes = 0;
                            await using (var destination = entry.Open())
                            {
                                var buffer = new byte[128 * 1024];
                                while (true)
                                {
                                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                                    if (read == 0) break;
                                    entryBytes += read;
                                    received += read;
                                    if (entryBytes > selected.SizeBytes || received > Mdf4ImportLimits.MaximumBytes)
                                        throw new InvalidDataException("De download overschrijdt de verwachte bestandsgrootte of importgrens.");
                                    hash.AppendData(buffer, 0, read);
                                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                                    progress?.Report(new OnlineDownloadProgress(received, total > 0 ? total : null));
                                }
                            }
                            var sha256 = Convert.ToHexString(hash.GetHashAndReset());
                            if (entryBytes != selected.SizeBytes ||
                                (selected.Sha256 is { } expectedHash && !string.Equals(expectedHash, sha256, StringComparison.OrdinalIgnoreCase)) ||
                                (plannedFile.Sha256 is { } plannedHash && !string.Equals(plannedHash, sha256, StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidDataException("De download is onvolledig of wijkt af van de verwachte SHA-256.");
                            manifestFiles.Add(CreateManifestFile(selected, archiveName, sha256));
                        }
                    }
                    // A mark changed during a long download must not publish a
                    // complete-looking archive under the superseded selection.
                    if (annotated is not null)
                        await PrepareAnnotatedDownloadAsync(cacheSelection, cancellationToken).ConfigureAwait(false);
                    var manifestEntry = archive.CreateEntry(OnlineLogArchiveManifestCodec.EntryName, CompressionLevel.Optimal);
                    await using var manifestStream = manifestEntry.Open();
                    await OnlineLogArchiveManifestCodec.WriteAsync(manifestStream,
                        new OnlineLogArchiveManifest { SchemaVersion = annotated is null ? 1 : 2,
                            Files = manifestFiles, OmittedFiles = annotated?.Omissions ?? [] }, cancellationToken).ConfigureAwait(false);
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(partialPath, finalPath, overwrite: true);
            CleanupCache(cacheDirectory, finalPath);
            return finalPath;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    public void Dispose()
    {
        // Closing CANalyser ends session-only logins; remembered logins keep their original deadline.
        _credentials = null;
        _httpClient.Dispose();
    }

    private DashboardCredentials? GetCurrentCredentials()
    {
        var credentials = _credentials;
        var now = _timeProvider.GetUtcNow();
        if (credentials?.Remembered is { } saved &&
            (saved.SavedAtUtc > now || saved.ExpiresAtUtc <= now))
        {
            _credentials = null;
            try { _credentialStore?.Clear(); }
            catch (OnlineLogCredentialStorageException) { }
            return null;
        }
        return credentials;
    }

    private HttpRequestMessage CreateDashboardRequest(HttpMethod method, string relativePath)
    {
        var uri = new Uri(_httpClient.BaseAddress ?? throw new InvalidOperationException("Het dashboardadres ontbreekt."), relativePath);
        var credentials = GetCurrentCredentials();
        if (credentials is not null && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Inloggen op het dashboard vereist een beveiligde HTTPS-verbinding.");
        var request = new HttpRequestMessage(method, uri);
        // Do not use default headers: this client also follows signed download URLs.
        if (credentials is not null) request.Headers.Authorization = credentials.Authorization;
        return request;
    }

    private async Task EnsureDashboardSuccessAsync(HttpRequestMessage request, HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var responseUri = response.RequestMessage?.RequestUri ?? request.RequestUri!;
        var dashboardUri = _httpClient.BaseAddress!;
        var sameOrigin = responseUri.Scheme == dashboardUri.Scheme && responseUri.Host == dashboardUri.Host &&
                         responseUri.Port == dashboardUri.Port;
        if (sameOrigin && response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
            response.Headers.WwwAuthenticate.Any(static challenge =>
                string.Equals(challenge.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)))
        {
            if (_credentials is { } rejected && request.Headers.Authorization?.Equals(rejected.Authorization) == true)
            {
                // Keep the user name for correction, but never retain a rejected saved login.
                rejected.Remembered = null;
                rejected.NeedsSave = false;
                try { _credentialStore?.Clear(); }
                catch (OnlineLogCredentialStorageException) { }
            }
            throw new OnlineLogAuthenticationException(request.Headers.Authorization is not null);
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var credentials = GetCurrentCredentials();
        if (sameOrigin && credentials is { NeedsSave: true, Remembered: { } saved } &&
            request.Headers.Authorization?.Equals(credentials.Authorization) == true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            credentials.NeedsSave = false;
            try { _credentialStore!.Save(saved); }
            catch (OnlineLogCredentialStorageException)
            {
                credentials.Remembered = null;
                throw;
            }
        }
    }

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
        var archiveName = string.Join('/', segments);
        if (!OnlineLogArchiveManifestCodec.IsSafeArchiveName(archiveName))
            throw new InvalidDataException("Het online-downloadplan bevat geen veilige MF4-bestandsnaam.");
        return archiveName;
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
        var identity = JsonSerializer.Serialize(new
        {
            schemaVersion = OnlineLogArchiveManifestCodec.CurrentSchemaVersion,
            files = files.OrderBy(static file => file.Key, StringComparer.Ordinal)
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return Path.Combine(directory, $"online-logs-{hash}.zip");
    }

    private string GetCacheDirectory() => _cacheDirectoryOverride ?? CanAnalyzer.Core.Storage.OnlineDownloadCache.DefaultDirectory;

    private static async Task<bool> IsUsableCachedArchiveAsync(string path, IReadOnlyList<OnlineLogSelection> files, CancellationToken cancellationToken,
        IReadOnlyList<OnlineLogArchiveOmission>? omissions = null)
    {
        if (!File.Exists(path)) return false;
        // S3 upload time has finite precision and is not an object revision.
        if (files.Any(static file => string.IsNullOrWhiteSpace(file.ETag) && string.IsNullOrWhiteSpace(file.ObjectVersion) && string.IsNullOrWhiteSpace(file.Sha256))) return false;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count != files.Count + 1 || archive.Entries.Select(static entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count) return false;
            var manifestEntry = archive.GetEntry(OnlineLogArchiveManifestCodec.EntryName);
            if (manifestEntry is null || manifestEntry.Length > OnlineLogArchiveManifestCodec.MaximumManifestBytes) return false;
            await using var manifestStream = manifestEntry.Open();
            var manifest = await OnlineLogArchiveManifestCodec.ReadAsync(manifestStream, cancellationToken).ConfigureAwait(false);
            if (manifest.Files.Count != files.Count) return false;
            if (!manifest.OmittedFiles.OrderBy(item => item.ObjectKey, StringComparer.Ordinal)
                    .SequenceEqual((omissions ?? []).OrderBy(item => item.ObjectKey, StringComparer.Ordinal))) return false;
            foreach (var file in files)
            {
                var metadata = manifest.Files.SingleOrDefault(item => item.ObjectKey == file.Key);
                if (metadata is null || metadata != CreateManifestFile(file, metadata.ArchiveName, metadata.Sha256) ||
                    (file.Sha256 is { } expectedHash && !string.Equals(metadata.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))) return false;
                var entry = archive.GetEntry(metadata.ArchiveName);
                if (entry is null || entry.Length != file.SizeBytes) return false;
                await using var source = entry.Open();
                await OnlineLogArchiveManifestCodec.ValidateFileAsync(source, metadata, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static OnlineLogArchiveFile CreateManifestFile(OnlineLogSelection file, string archiveName, string sha256) => new()
    {
        ArchiveName = archiveName, ObjectKey = file.Key, LoggerId = file.Logger, Session = file.Session,
        RawRecordedAtUtc = file.RawRecordedAtUtc, UploadedAtUtc = file.UploadedAtUtc,
        ClockQuality = NormalizeClockQuality(file.ClockQuality, file.ClockEvidence, file.RawRecordedAtUtc), ClockEvidence = file.ClockEvidence,
        MachineName = file.MachineName, MachineId = file.MachineId, InstallationId = file.InstallationId, ConfigurationId = file.ConfigurationId,
        MachineAssignmentQuality = NormalizeMachineQuality(file.MachineAssignmentQuality, file.MachineAssignmentEvidence, file.MachineId, file.InstallationId),
        MachineAssignmentEvidence = file.MachineAssignmentEvidence,
        ObjectVersion = file.ObjectVersion, MetadataRevision = file.MetadataRevision, Sha256 = sha256, SizeBytes = file.SizeBytes
    };

    private static string NormalizeClockQuality(string? quality, string? evidence, DateTimeOffset? rawTime) =>
        quality == "suspect" ? "suspect" : quality == "verified" && rawTime.HasValue && !string.IsNullOrWhiteSpace(evidence) ? "verified" : "unverified";

    private static string NormalizeMachineQuality(string? quality, string? evidence, string? machineId, string? installationId) =>
        quality == "verified-installation" && !string.IsNullOrWhiteSpace(evidence) && !string.IsNullOrWhiteSpace(machineId) &&
        !string.IsNullOrWhiteSpace(installationId) ? "verified-installation" : "unverified";

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

    private sealed record LogListResponse(LogFileResponse[] Files, bool Truncated, int MaximumSelection, string? TimeBasis, int UnknownRecordingTimes,
        LogCapabilities? Capabilities);
    private sealed record LogCapabilities(AnnotationCapabilities? Annotations, int DownloadManifestVersion);
    private sealed record AnnotationCapabilities(bool Available, int Version, bool Shared);
    private sealed record DownloadPlanResponse(DownloadFileResponse[]? Files, DateTimeOffset ExpiresAt,
        int SchemaVersion = 1, OnlineLogArchiveOmission[]? OmittedFiles = null);
    private sealed record DownloadFileResponse(string? ArchiveName, string? Url, string? Key, long? SizeBytes, string? ObjectVersion, string? Sha256,
        string? ETag, string? VersionId, string? MetadataRevision, DateTimeOffset? UploadedAt,
        string? AnnotationRevision = null);
    private sealed record LogFileResponse(
        string Key,
        string Name,
        string Machine,
        string Logger,
        string Session,
        DateTimeOffset CreatedAt,
        long SizeBytes,
        DateTimeOffset? RecordedAt,
        DateTimeOffset? UploadedAt,
        string? ClockQuality, string? ClockEvidence, string? MachineAssignmentQuality, string? MachineAssignmentEvidence,
        string? MachineId, string? InstallationId, string? ConfigurationId, string? ObjectVersion, string? ETag, string? MetadataRevision, string? Sha256,
        OnlineLogAnnotation? Annotation);
    private sealed record ErrorResponse(string Error);

    // A class deliberately avoids a generated record ToString containing authentication data.
    private sealed class DashboardCredentials(string userName, AuthenticationHeaderValue authorization)
    {
        public string UserName { get; } = userName;
        public AuthenticationHeaderValue Authorization { get; } = authorization;
        public RememberedOnlineLogCredentials? Remembered { get; set; }
        public bool NeedsSave { get; set; }
    }
}
