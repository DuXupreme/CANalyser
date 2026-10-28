using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Parsing;

/// <summary>Bounded, read-only CANedge header provenance. Never derives machine identity from its clock.</summary>
public static partial class Mdf4MetadataReader
{
    public static SourceLogFile Read(string path, SourceLogFile source)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 168) return source;
        Span<byte> header = stackalloc byte[168];
        stream.ReadExactly(header);
        if (!(header[..8].SequenceEqual("MDF     "u8) || header[..8].SequenceEqual("UnFinMF "u8)) ||
            !header.Slice(16, 2).SequenceEqual("CE"u8) || !header.Slice(64, 4).SequenceEqual("##HD"u8))
            return source;
        if (BinaryPrimitives.ReadUInt64LittleEndian(header[72..]) != 104 ||
            BinaryPrimitives.ReadUInt64LittleEndian(header[80..]) != 6)
            throw new InvalidDataException("Ongeldige CANedge MF4-headerstructuur.");
        var address = BinaryPrimitives.ReadUInt64LittleEndian(header[128..]);
        if (address == 0) return source;
        if (address > (ulong)(stream.Length - 24)) throw new InvalidDataException("MF4-metadata valt buiten het bestand.");
        stream.Position = (long)address;
        Span<byte> block = stackalloc byte[24];
        stream.ReadExactly(block);
        var length = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
        if (!block[..4].SequenceEqual("##MD"u8) || length < 24 || length > 1_048_576 ||
            length > (ulong)(stream.Length - (long)address) || BinaryPrimitives.ReadUInt64LittleEndian(block[16..]) != 0)
            throw new InvalidDataException("Ongeldige of te grote MF4-metadata.");
        var bytes = new byte[(int)length - 24];
        stream.ReadExactly(bytes);
        using var text = new StringReader(Encoding.UTF8.GetString(bytes).TrimEnd('\0'));
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576
        });
        XDocument document;
        try { document = XDocument.Load(reader); }
        catch (XmlException ex) { throw new InvalidDataException("Ongeldige MF4-header-XML.", ex); }
        if (document.Root?.Name.LocalName != "HDcomment") return source;
        string? Property(string tree, string name)
        {
            var values = document.Descendants()
                .Where(e => e.Name.LocalName == "tree" && (string?)e.Attribute("name") == tree)
                .SelectMany(e => e.Elements())
                .Where(e => e.Name.LocalName == "e" && (string?)e.Attribute("name") == name)
                .Select(e => e.Value.Trim()).Take(2).ToArray();
            if (values.Length > 1) throw new InvalidDataException("Dubbele identiteitsvelden in MF4-metadata.");
            return values.SingleOrDefault();
        }
        var logger = Property("Device Information", "serial number");
        if (logger is not { Length: 8 } || !logger.All(Uri.IsHexDigit)) return source;
        logger = logger.ToUpperInvariant();
        if (source.Logger is { Length: 8 } expected && expected.All(Uri.IsHexDigit) &&
            !string.Equals(expected, logger, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Logger-ID in MF4-header verschilt van de bronidentiteit.");
        var session = Property("File Information", "session");
        if (session is { Length: > 0 and <= 8 } && session.All(Uri.IsHexDigit)) session = session.PadLeft(8, '0').ToUpperInvariant();
        else session = source.Session;
        if (source.Session is { Length: 8 } expectedSession && expectedSession.All(Uri.IsHexDigit) &&
            !string.Equals(expectedSession, session, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sessienummer in MF4-header verschilt van de bronidentiteit.");
        var metadata = Property("File Information", "comment");
        var match = InstallationCode().Match(metadata ?? "");
        var verifiedInstallation = source.MachineAssignmentQuality == "verified-installation" &&
            !string.IsNullOrWhiteSpace(source.MachineId) && !string.IsNullOrWhiteSpace(source.InstallationId) &&
            !string.IsNullOrWhiteSpace(source.MachineAssignmentEvidence);
        if (verifiedInstallation && match.Success &&
            (source.MachineId != match.Groups[1].Value || source.InstallationId != match.Groups[2].Value ||
             source.ConfigurationId is { Length: > 0 } revision && revision != match.Groups[3].Value))
            throw new InvalidDataException("Installatie-identiteit in MF4-header verschilt van de geverifieerde bronmetadata.");
        DateTimeOffset? origin = null;
        var ns = BinaryPrimitives.ReadUInt64LittleEndian(header[136..]);
        try { origin = DateTimeOffset.UnixEpoch.AddTicks(checked((long)(ns / 100))); }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException) { }
        return source with
        {
            Logger = logger, Session = session, DeviceMetadata = metadata,
            ConfigurationCrc32 = Property("Device Information", "config crc32 checksum"),
            FirmwareVersion = Property("Device Information", "firmware version"), LoggerStartTimeUtc = origin,
            MachineId = verifiedInstallation ? source.MachineId : match.Success ? match.Groups[1].Value : null,
            InstallationId = verifiedInstallation ? source.InstallationId : match.Success ? match.Groups[2].Value : null,
            ConfigurationId = verifiedInstallation ? source.ConfigurationId : match.Success ? match.Groups[3].Value : null,
            MachineAssignmentQuality = verifiedInstallation ? "verified-installation" : match.Success ? "embedded-installation" : "unknown",
            MachineAssignmentEvidence = verifiedInstallation ? source.MachineAssignmentEvidence : match.Success ? "MF4 HDcomment File Information/comment" : null
        };
    }

    [GeneratedRegex(@"\A(M[0-9]{2});(I[0-9]{4});(C[0-9]{4})\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstallationCode();
}
