using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Infrastructure;

public sealed partial class OnlineLogService
{
    public async Task<IReadOnlyList<OnlineLogAnnotationResult>> UpdateAnnotationsAsync(
        IReadOnlyList<OnlineLogAnnotationUpdate> updates, CancellationToken cancellationToken)
    {
        if (updates.Count is < 1 or > 25 || updates.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != updates.Count ||
            updates.Any(item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.ExpectedObjectVersion) ||
                item.Excluded && string.IsNullOrWhiteSpace(item.Reason) || item.Reason.Length > 2000))
            throw new ArgumentException("Kies 1 tot 25 bestanden met een actuele versie en vul bij markeren een reden in.", nameof(updates));
        using var request = CreateDashboardRequest(HttpMethod.Post, "api/log-annotations");
        request.Content = JsonContent.Create(new { updates });
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureDashboardSuccessAsync(request, response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<AnnotationResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result?.Annotations is not { } annotations || annotations.Length != updates.Count ||
            annotations.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != updates.Count ||
            annotations.Any(item => !updates.Any(update => update.Key == item.Key && update.ExpectedObjectVersion == item.ObjectVersion &&
                update.Excluded == item.Excluded) || string.IsNullOrWhiteSpace(item.Revision)))
            throw new InvalidDataException("De bevestiging van de markeringen is onvolledig. Vernieuw de loglijst.");
        return annotations;
    }

    private async Task<DownloadPlanResponse> RequestDownloadPlanAsync(string[] keys, CancellationToken cancellationToken,
        IReadOnlyList<OnlineLogSelection>? expected = null)
    {
        using var request = CreateDashboardRequest(HttpMethod.Post, "api/download-plan");
        request.Content = expected is null ? JsonContent.Create(new { keys }) : JsonContent.Create(new
        {
            keys, includeExcluded = false,
            expectedObjects = expected.Select(file => new { key = file.Key, objectVersion = file.ObjectVersion }),
            expectedAnnotations = expected.Select(file => new { key = file.Key, revision = file.Annotation!.Revision })
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureDashboardSuccessAsync(request, response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DownloadPlanResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Het online-downloadplan is leeg.");
    }

    private async Task<AnnotatedDownload> PrepareAnnotatedDownloadAsync(IReadOnlyList<OnlineLogSelection> files,
        CancellationToken cancellationToken)
    {
        if (files.Any(file => file.Annotation is null || string.IsNullOrWhiteSpace(file.ObjectVersion)))
            throw new InvalidDataException("De markering of bestandsversie is onbekend. Vernieuw de loglijst.");
        var downloads = new Dictionary<string, DownloadFileResponse>(StringComparer.Ordinal);
        var omissions = new List<OnlineLogArchiveOmission>();
        foreach (var batch in files.Chunk(200))
        {
            var plan = await RequestDownloadPlanAsync(batch.Select(file => file.Key).ToArray(), cancellationToken, batch).ConfigureAwait(false);
            if (plan.SchemaVersion != 2 || plan.Files is null || plan.OmittedFiles is null ||
                plan.Files.Length + plan.OmittedFiles.Length != batch.Length)
                throw new InvalidDataException("Het dashboard moet alle opgenomen en uitgesloten bestanden in manifest versie 2 verantwoorden.");
            var remaining = batch.ToDictionary(file => file.Key, StringComparer.Ordinal);
            foreach (var included in plan.Files)
            {
                if (included.Key is not { } key || !remaining.Remove(key, out var selected) || selected.Annotation!.Excluded ||
                    included.ObjectVersion != selected.ObjectVersion || included.AnnotationRevision != selected.Annotation.Revision)
                    throw new InvalidDataException("Een bestand of markering is gewijzigd. Vernieuw de loglijst.");
                if (included.SizeBytes != selected.SizeBytes ||
                    included.MetadataRevision is { } revision && selected.MetadataRevision is { } expectedRevision && revision != expectedRevision ||
                    included.ETag is { } etag && selected.ETag is { } expectedETag && etag != expectedETag ||
                    included.Sha256 is { } hash && (!OnlineLogArchiveManifestCodec.IsSha256(hash) ||
                        selected.Sha256 is { } expectedHash && !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase)) ||
                    included.UploadedAt is { } received && selected.UploadedAtUtc is { } expectedReceipt && received != expectedReceipt)
                    throw new InvalidDataException("De online bronmetadata is gewijzigd. Vernieuw de loglijst.");
                downloads.Add(key, included);
            }
            foreach (var omitted in plan.OmittedFiles)
            {
                if (!remaining.Remove(omitted.ObjectKey, out var selected) || !selected.Annotation!.Excluded ||
                    omitted.ObjectVersion != selected.ObjectVersion || omitted.AnnotationRevision != selected.Annotation.Revision ||
                    omitted.Reason != selected.Annotation.Reason || string.IsNullOrWhiteSpace(omitted.Reason) ||
                    string.IsNullOrWhiteSpace(omitted.AnnotationRevision) || omitted.SizeBytes != selected.SizeBytes ||
                    omitted.LoggerId != selected.Logger || omitted.Session != selected.Session ||
                    SourceLogFile.FromArchiveEntry(omitted.ArchiveName).Name != selected.Name ||
                    ValidateArchiveName(omitted.ArchiveName) != omitted.ArchiveName)
                    throw new InvalidDataException("Een uitgesloten bestand of markering is gewijzigd. Vernieuw de loglijst.");
                omissions.Add(omitted);
            }
            if (remaining.Count != 0) throw new InvalidDataException("Het downloadplan mist geselecteerde bestanden.");
        }
        return new(downloads, omissions);
    }

    private sealed record AnnotatedDownload(IReadOnlyDictionary<string, DownloadFileResponse> Downloads,
        IReadOnlyList<OnlineLogArchiveOmission> Omissions);
    private sealed record AnnotationResponse(OnlineLogAnnotationResult[] Annotations);
}
