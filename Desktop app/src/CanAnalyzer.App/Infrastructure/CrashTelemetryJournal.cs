using System.IO;
using System.Text;

namespace CanAnalyzer.App.Infrastructure;

/// <summary>Durable session marker. An exclusive handle distinguishes running instances from abandoned sessions.</summary>
public sealed class CrashTelemetryJournal : IDisposable
{
    private readonly string _directory;
    private readonly object _sync = new();
    private FileStream? _session;
    private string? _path;
    private bool _crashed;

    public CrashTelemetryJournal(string directory) => _directory = directory;

    public void Start(string sessionId, string unexpectedExitPayload)
    {
        lock (_sync)
        {
            if (_session is not null) return;
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, sessionId + ".json");
            _session = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            WritePayload(unexpectedExitPayload);
        }
    }

    public void RecordCrash(string payload)
    {
        lock (_sync)
        {
            if (_session is null || _crashed) return;
            _crashed = true;
            WritePayload(payload);
        }
    }

    private void WritePayload(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        _session!.Position = 0;
        _session.Write(bytes);
        _session.SetLength(bytes.Length);
        _session.Flush(flushToDisk: true);
    }

    public async Task ReplayAsync(Func<string, Task<bool>> send)
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            // Hold the handle through acknowledgement, also preventing concurrent replay.
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (await send(payload).ConfigureAwait(false))
                {
                    stream.Dispose();
                    File.Delete(path);
                }
            }
            catch (IOException) { /* Active instance, concurrent replay, or unavailable storage: retry next start. */ }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _session?.Dispose();
            _session = null;
            if (!_crashed && _path is not null) File.Delete(_path);
        }
    }
}
