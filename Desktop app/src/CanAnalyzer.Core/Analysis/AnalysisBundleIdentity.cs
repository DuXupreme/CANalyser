using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

public static class AnalysisBundleIdentity
{
    public static string SignalId(SignalIdentity identity, string unit) => Hash(JsonSerializer.Serialize(new
    {
        identity.Channel, identity.FrameFormat, identity.IsExtended, identity.FrameId,
        identity.MessageName, identity.SignalName, Unit = unit
    }));

    public static string? SourceId(SourceLogFile source) => !OnlineLogArchiveManifestCodec.IsSha256(source.Sha256)
        ? null : "sha256:" + source.Sha256!.ToUpperInvariant();

    public static string[] SourceIds(CanDataset dataset) => dataset.SourceFiles.Select(SourceId)
        .Where(id => id is not null).Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
