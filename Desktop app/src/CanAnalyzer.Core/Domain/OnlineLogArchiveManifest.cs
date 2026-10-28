using System.Security.Cryptography;
using System.Text.Json;

namespace CanAnalyzer.Core.Domain;

/// <summary>Provenance of downloaded bytes. A checksum is not evidence of clock accuracy.</summary>
public sealed record OnlineLogArchiveManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string MetadataRevision { get; init; } = "1";
    public IReadOnlyList<OnlineLogArchiveFile> Files { get; init; } = [];
    public IReadOnlyList<OnlineLogArchiveOmission> OmittedFiles { get; init; } = [];
}

/// <summary>A deliberately excluded immutable object; its bytes are never part of the archive.</summary>
public sealed record OnlineLogArchiveOmission
{
    public string ArchiveName { get; init; } = "";
    public string ObjectKey { get; init; } = "";
    public string LoggerId { get; init; } = "";
    public string Session { get; init; } = "";
    public string ObjectVersion { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Reason { get; init; } = "";
    public string AnnotationRevision { get; init; } = "";
}

public sealed record OnlineLogArchiveFile
{
    public string ArchiveName { get; init; } = "";
    public string ObjectKey { get; init; } = "";
    public string LoggerId { get; init; } = "";
    public string Session { get; init; } = "";
    public DateTimeOffset? RawRecordedAtUtc { get; init; }
    public DateTimeOffset? UploadedAtUtc { get; init; }
    public string ClockQuality { get; init; } = "unverified";
    public string? ClockEvidence { get; init; }
    public string? MachineName { get; init; }
    public string? MachineId { get; init; }
    public string? InstallationId { get; init; }
    public string? ConfigurationId { get; init; }
    public string MachineAssignmentQuality { get; init; } = "unverified";
    public string? MachineAssignmentEvidence { get; init; }
    public string? ObjectVersion { get; init; }
    public string? MetadataRevision { get; init; }
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
}

public static class OnlineLogArchiveManifestCodec
{
    public const string EntryName = "canalyser-log-manifest.json";
    public const int CurrentSchemaVersion = 2;
    public const long MaximumManifestBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<OnlineLogArchiveManifest> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Bound even a non-seekable/decompression stream before deserialization.
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumManifestBytes) throw new InvalidDataException("Het logmanifest is te groot.");
            buffer.Write(bytes, 0, read);
        }
        buffer.Position = 0;
        OnlineLogArchiveManifest manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<OnlineLogArchiveManifest>(buffer, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Het logmanifest is leeg.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Het logmanifest is ongeldig.", ex); }
        ValidateManifest(manifest);
        return manifest;
    }

    public static async Task WriteAsync(Stream stream, OnlineLogArchiveManifest manifest, CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        // Validate the encoded size before touching the archive entry. The reader must
        // always be able to reopen a manifest that this writer publishes.
        using var buffer = new BoundedManifestBuffer();
        await JsonSerializer.SerializeAsync(buffer, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        await buffer.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private sealed class BoundedManifestBuffer : MemoryStream
    {
        private void Reserve(int count)
        {
            var end = Position + count;
            if (end > MaximumManifestBytes) throw new InvalidDataException("Het logmanifest is te groot.");
            if (end > Capacity) Capacity = (int)Math.Min(MaximumManifestBytes, Math.Max(end, Capacity * 2L));
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Reserve(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }

    public static void ValidateManifest(OnlineLogArchiveManifest manifest)
    {
        if (manifest.SchemaVersion is not (1 or CurrentSchemaVersion) || string.IsNullOrWhiteSpace(manifest.MetadataRevision))
            throw new InvalidDataException("De versie van het logmanifest wordt niet ondersteund.");
        if (manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > Mdf4ImportLimits.MaximumFiles)
            throw new InvalidDataException("Het logmanifest bevat een ongeldig aantal bestanden.");
        if (manifest.OmittedFiles is null || manifest.Files.Count + manifest.OmittedFiles.Count > Mdf4ImportLimits.MaximumFiles ||
            (manifest.SchemaVersion == 1 && manifest.OmittedFiles.Count > 0))
            throw new InvalidDataException("Uitgesloten bestanden vereisen logmanifest versie 2 en een geldige selectiegrootte.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || !IsSafeArchiveName(file.ArchiveName) || !names.Add(file.ArchiveName) ||
                string.IsNullOrWhiteSpace(file.ObjectKey) || !keys.Add(file.ObjectKey) ||
                string.IsNullOrWhiteSpace(file.LoggerId) || string.IsNullOrWhiteSpace(file.Session) ||
                file.SizeBytes < 0 || file.SizeBytes > Mdf4ImportLimits.MaximumFileBytes || !IsSha256(file.Sha256))
                throw new InvalidDataException("Het logmanifest bevat ongeldige of dubbele bestandsgegevens.");
            total += file.SizeBytes;
            if (total > Mdf4ImportLimits.MaximumBytes) throw new InvalidDataException("Het logmanifest overschrijdt de importgrens.");
            if (file.ClockQuality is not ("unverified" or "suspect" or "verified") ||
                (file.ClockQuality == "verified" && (file.RawRecordedAtUtc is null || string.IsNullOrWhiteSpace(file.ClockEvidence))) ||
                file.MachineAssignmentQuality is not ("unverified" or "verified-installation") ||
                (file.MachineAssignmentQuality == "verified-installation" &&
                 (string.IsNullOrWhiteSpace(file.MachineName) || string.IsNullOrWhiteSpace(file.MachineId) ||
                  string.IsNullOrWhiteSpace(file.InstallationId) || string.IsNullOrWhiteSpace(file.MachineAssignmentEvidence))))
                throw new InvalidDataException("Het logmanifest bevat een onbewezen tijd- of machinetoewijzing.");
        }
        foreach (var file in manifest.OmittedFiles)
        {
            if (file is null || !IsSafeArchiveName(file.ArchiveName) || !names.Add(file.ArchiveName) ||
                string.IsNullOrWhiteSpace(file.ObjectKey) || !keys.Add(file.ObjectKey) ||
                string.IsNullOrWhiteSpace(file.LoggerId) || string.IsNullOrWhiteSpace(file.Session) ||
                string.IsNullOrWhiteSpace(file.ObjectVersion) || file.ObjectVersion.Equals("null", StringComparison.OrdinalIgnoreCase) || file.ObjectVersion.Length > 1024 ||
                file.SizeBytes < 0 || file.SizeBytes > Mdf4ImportLimits.MaximumFileBytes ||
                string.IsNullOrWhiteSpace(file.Reason) || file.Reason.Length > 2000 ||
                string.IsNullOrWhiteSpace(file.AnnotationRevision) || file.AnnotationRevision.Length > 256)
                throw new InvalidDataException("Een uitgesloten bestand mist een unieke bron, objectversie, reden of annotatierevisie.");
        }
        if (manifest.SchemaVersion == 2)
        {
            var identities = manifest.Files.Select(file => Identity(file.ArchiveName, file.LoggerId, file.Session))
                .Concat(manifest.OmittedFiles.Select(file => Identity(file.ArchiveName, file.LoggerId, file.Session))).ToArray();
            var validation = OnlineLogSequencePolicy.Validate(identities);
            if (!validation.IsValid) throw new InvalidDataException(validation.Message);
            if (manifest.OmittedFiles.Count > 0 && identities.Any(file =>
                    !uint.TryParse(file.Session, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out _)))
                throw new InvalidDataException("Met uitsluitingen is een herkenbaar CANedge-sessienummer nodig om segmentgrenzen te bewijzen.");
        }
    }

    private static OnlineLogPartIdentity Identity(string archiveName, string logger, string session)
    {
        var identity = SourceLogFile.FromArchiveEntry(archiveName);
        if (!string.Equals(identity.Logger, logger, StringComparison.Ordinal) ||
            !string.Equals(identity.Session, session, StringComparison.Ordinal))
            throw new InvalidDataException("Het archiefpad en de logger-/sessie-identiteit in het manifest verschillen.");
        return new(logger, session, identity.Name);
    }

    public static async Task ValidateFileAsync(Stream stream, OnlineLogArchiveFile file, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long size = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            size += read;
            if (size > file.SizeBytes) throw new InvalidDataException("Een logbestand wijkt af van het manifest (grootte).");
            hash.AppendData(buffer, 0, read);
        }
        if (size != file.SizeBytes || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Een logbestand wijkt af van het manifest (grootte of SHA-256).");
    }

    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static character => char.IsAsciiHexDigit(character));

    public static bool IsSafeArchiveName(string? value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Any(static character => character is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*' || char.IsControl(character)) &&
        value.EndsWith(".mf4", StringComparison.OrdinalIgnoreCase) &&
        value.Split('/').All(static segment => segment.Length > 0 && segment is not ("." or "..") &&
            !segment.EndsWith('.') && !segment.EndsWith(' '));
}
