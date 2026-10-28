using System.IO.Compression;
using System.Text.Json;

namespace CanAnalyzer.Core.Analysis;

/// <summary>Reads only comparison inputs into a bounded DOM, while retaining exact accepted delays.</summary>
internal static class AnalysisComparisonJsonProjection
{
    private const long MaximumInputBytes = 512L * 1024 * 1024;
    private const int MaximumTokenBytes = 1024 * 1024;
    private const int MaximumEvents = 2_000_000;
    private const int MaximumProjectionBytes = 64 * 1024 * 1024;

    public static JsonDocument Read(ZipArchive zip, string name, CancellationToken token)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return JsonDocument.Parse("{}");
        if (entry.Length > MaximumInputBytes) throw new InvalidDataException("Analyseonderdeel is te groot: " + name);
        using var input = entry.Open();
        using var output = new LimitedMemoryStream(MaximumProjectionBytes);
        using var writer = new Utf8JsonWriter(output);
        var buffer = new byte[65536];
        var count = 0; var final = false; long total = 0;
        var readerState = new JsonReaderState(new() { MaxDepth = 64 });
        var containers = new Stack<(string Path, bool Array)>();
        string? pendingPath = null;
        var skipValue = false; var skipDepth = 0; var events = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!final)
            {
                var read = input.Read(buffer, count, buffer.Length - count);
                total += read;
                if (total > MaximumInputBytes) throw new InvalidDataException("Analyseonderdeel overschrijdt de leeslimiet.");
                count += read; final = read == 0;
            }
            var reader = new Utf8JsonReader(buffer.AsSpan(0, count), final, readerState);
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (skipValue)
                {
                    skipValue = false;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) skipDepth = 1;
                    continue;
                }
                if (skipDepth > 0)
                {
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) skipDepth++;
                    else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) skipDepth--;
                    continue;
                }
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var property = reader.GetString()!;
                    var parent = containers.Peek().Path;
                    pendingPath = parent.Length == 0 ? property : parent + "." + property;
                    if (Omit(pendingPath)) { skipValue = true; pendingPath = null; }
                    else writer.WritePropertyName(property);
                    continue;
                }
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        var path = containers.Count > 0 && containers.Peek().Array ? containers.Peek().Path + "[]" : pendingPath ?? "";
                        if (reader.TokenType == JsonTokenType.StartObject && path.EndsWith(".Events[]", StringComparison.Ordinal) && ++events > MaximumEvents)
                            throw new InvalidDataException("Te veel reactietijdevents voor vergelijking.");
                        containers.Push((path, reader.TokenType == JsonTokenType.StartArray)); pendingPath = null;
                        if (reader.TokenType == JsonTokenType.StartObject) writer.WriteStartObject(); else writer.WriteStartArray();
                        break;
                    case JsonTokenType.EndObject: containers.Pop(); writer.WriteEndObject(); break;
                    case JsonTokenType.EndArray: containers.Pop(); writer.WriteEndArray(); break;
                    case JsonTokenType.String: writer.WriteStringValue(reader.GetString()); break;
                    case JsonTokenType.Number: writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true); break;
                    case JsonTokenType.True: writer.WriteBooleanValue(true); break;
                    case JsonTokenType.False: writer.WriteBooleanValue(false); break;
                    case JsonTokenType.Null: writer.WriteNullValue(); break;
                }
            }
            writer.Flush();
            readerState = reader.CurrentState;
            var consumed = checked((int)reader.BytesConsumed);
            count -= consumed;
            if (count > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, count);
            if (final) break;
            if (count == buffer.Length)
            {
                if (buffer.Length >= MaximumTokenBytes) throw new InvalidDataException("Een JSON-waarde overschrijdt de tokenlimiet.");
                Array.Resize(ref buffer, Math.Min(MaximumTokenBytes, buffer.Length * 2));
            }
        }
        token.ThrowIfCancellationRequested();
        return JsonDocument.Parse(output.GetBuffer().AsMemory(0, checked((int)output.Length)));
    }

    private static bool Omit(string path)
    {
        if (path is "CanChannels" or "BatteryConfiguration" or "Quality" or
            "Profile.SignalDefinitions" or "Profile.MappingCandidates" or "Profile.InvalidatedSignals" or
            "Joysticks[].Value.RadiusDistribution" or "Joysticks[].Value.InputEpisodes" or "Joysticks[].Value.FullHolds" or
            "Actuators[].Value.Stroke") return true;
        foreach (var reference in new[] { "SetpointChange", "PositionRequest" })
        {
            var prefix = "Actuators[].Value." + reference + ".Value.Events[].";
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                return path[prefix.Length..] is not ("Outcome" or "DelaySeconds");
        }
        return false;
    }

    private sealed class LimitedMemoryStream(int limit) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit) throw new InvalidDataException("De benodigde vergelijkingsgegevens overschrijden de geheugenlimiet.");
            base.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > limit) throw new InvalidDataException("De benodigde vergelijkingsgegevens overschrijden de geheugenlimiet.");
            base.Write(buffer, offset, count);
        }
    }
}
