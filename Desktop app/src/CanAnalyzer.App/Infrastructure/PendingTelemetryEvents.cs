using System.IO;
using System.Text;
using System.Text.Json;

namespace CanAnalyzer.App.Infrastructure;

/// <summary>Small, durable queue for privacy-minimal diagnostics that must survive forced exit.</summary>
public sealed class PendingTelemetryEvents(string directory, int maximumEvents = 32)
{
    private const int MaximumPayloadBytes = 16 * 1024;
    private readonly object _writes = new();
    private int _replaying;

    public bool Record(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumPayloadBytes) return false;
        lock (_writes)
        {
            Directory.CreateDirectory(directory);
            if (Directory.EnumerateFiles(directory, "*.json").Take(maximumEvents).Count() >= maximumEvents)
                return false;
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            var temporaryPath = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Delete))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                    // Keep ownership through publication so orphan cleanup in another
                    // instance cannot claim a completed but not-yet-renamed temporary file.
                    File.Move(temporaryPath, path + ".json");
                }
                // Publish only the fully flushed file. A forced exit during the write must
                // never turn a partial payload into the head of the replay queue.
                return true;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    public async Task ReplayAsync(Func<string, CancellationToken, Task<bool>> send,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _replaying, 1) != 0) return;
        try
        {
            if (!Directory.Exists(directory)) return;
            // A killed writer can leave its unpublished temporary file behind. Active
            // writers keep an exclusive handle, so only abandoned files can be removed.
            foreach (var path in Directory.EnumerateFiles(directory, "*.tmp").Take(maximumEvents))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    File.Delete(path);
                }
                catch (IOException) { /* Another instance is still writing this event. */ }
            }
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(maximumEvents))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Exclusive ownership also prevents duplicate sends from another running instance.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    if (stream.Length > MaximumPayloadBytes)
                    {
                        stream.Dispose();
                        File.Delete(path);
                        continue;
                    }
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    if (!IsValidObject(json))
                    {
                        stream.Dispose();
                        File.Delete(path);
                        continue;
                    }
                    if (!await send(json, cancellationToken).ConfigureAwait(false)) break;
                    stream.Dispose();
                    File.Delete(path);
                }
                catch (IOException) { /* Active writer/replay or unavailable storage: retry next start. */ }
            }
        }
        finally { Volatile.Write(ref _replaying, 0); }
    }

    private static bool IsValidObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }
}
