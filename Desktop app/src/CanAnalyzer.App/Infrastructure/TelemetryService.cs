using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using Microsoft.Extensions.Logging;

namespace CanAnalyzer.App.Infrastructure;

/// <inheritdoc />
public sealed class TelemetryService : ITelemetryService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private readonly ILogger<TelemetryService> _logger;
    private readonly object _sync = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private TelemetryOptions _options = new();
    private DateTime _lastRetentionCleanupUtc = DateTime.MinValue;

    public TelemetryService(ILogger<TelemetryService> logger)
    {
        _logger = logger;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanAnalyzer");
        LocalLogPath = Path.Combine(root, "telemetry-events.jsonl");
    }

    public string LocalLogPath { get; }

    public string InstallationId
    {
        get
        {
            lock (_sync)
            {
                return _options.InstallationId;
            }
        }
    }

    public void Configure(TelemetryOptions options)
    {
        lock (_sync)
        {
            _options = CopyOptions(options);
        }
    }

    public async Task TrackEventAsync(
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        CancellationToken cancellationToken = default)
    {
        TelemetryOptions options;
        lock (_sync)
        {
            options = CopyOptions(_options);
        }

        if (!options.Enabled || string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        try
        {
            await PruneLocalLogAsync(options, cancellationToken).ConfigureAwait(false);

            var payload = CreatePayload(eventName, options, properties);
            var json = JsonSerializer.Serialize(payload, SerializerOptions);
            await AppendLocalAsync(json, cancellationToken).ConfigureAwait(false);
            await PostRemoteAsync(json, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telemetry event '{EventName}' could not be recorded.", eventName);
        }
    }

    private Dictionary<string, object?> CreatePayload(
        string eventName,
        TelemetryOptions options,
        IReadOnlyDictionary<string, object?>? properties)
    {
        return new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["event_id"] = Guid.NewGuid().ToString("N"),
            ["event_name"] = eventName.Trim(),
            ["timestamp_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["app_version"] = GetAppVersion(),
            ["installation_id"] = options.InstallationId,
            ["session_id"] = _sessionId,
            ["os_description"] = RuntimeInformation.OSDescription,
            ["process_architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["runtime_version"] = Environment.Version.ToString(),
            ["properties"] = SanitizeProperties(properties)
        };
    }

    private static IReadOnlyDictionary<string, object?> SanitizeProperties(
        IReadOnlyDictionary<string, object?>? properties)
    {
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        if (properties is null)
        {
            return result;
        }

        foreach (var (key, value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            result[SanitizeKey(key)] = value switch
            {
                null => null,
                bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
                Enum enumValue => enumValue.ToString(),
                TimeSpan timeSpan => (long)timeSpan.TotalMilliseconds,
                string text => Truncate(text, 120),
                _ => value.GetType().Name
            };
        }

        return result;
    }

    private static string SanitizeKey(string key)
    {
        var builder = new StringBuilder(capacity: Math.Min(key.Length, 64));
        foreach (var ch in key.Trim().Take(64))
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return builder.Length == 0 ? "property" : builder.ToString();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private async Task AppendLocalAsync(string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(LocalLogPath)
                        ?? throw new InvalidOperationException("Telemetry log path has no directory.");
        Directory.CreateDirectory(directory);
        await File.AppendAllTextAsync(LocalLogPath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private async Task PostRemoteAsync(string json, TelemetryOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.EndpointUrl))
        {
            return;
        }

        if (!Uri.TryCreate(options.EndpointUrl.Trim(), UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(options.EndpointKey))
        {
            request.Headers.TryAddWithoutValidation("X-CANalyser-Telemetry-Key", options.EndpointKey.Trim());
        }

        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Telemetry endpoint returned HTTP {StatusCode}.", response.StatusCode);
        }
    }

    private async Task PruneLocalLogAsync(TelemetryOptions options, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (now - _lastRetentionCleanupUtc < TimeSpan.FromHours(12) || !File.Exists(LocalLogPath))
        {
            return;
        }

        _lastRetentionCleanupUtc = now;
        var retentionDays = Math.Clamp(options.RetentionDays, 30, 730);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var lines = await File.ReadAllLinesAsync(LocalLogPath, cancellationToken).ConfigureAwait(false);
        var retained = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || ShouldRetain(line, cutoff))
            {
                retained.Add(line);
            }
        }

        await File.WriteAllLinesAsync(LocalLogPath, retained, cancellationToken).ConfigureAwait(false);
    }

    private static bool ShouldRetain(string line, DateTimeOffset cutoff)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("timestamp_utc", out var timestampElement) ||
                !timestampElement.TryGetDateTimeOffset(out var timestamp))
            {
                return true;
            }

            return timestamp >= cutoff;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static TelemetryOptions CopyOptions(TelemetryOptions source)
    {
        return new TelemetryOptions
        {
            Enabled = source.Enabled,
            EndpointUrl = source.EndpointUrl ?? string.Empty,
            EndpointKey = source.EndpointKey ?? string.Empty,
            InstallationId = string.IsNullOrWhiteSpace(source.InstallationId)
                ? Guid.NewGuid().ToString("N")
                : source.InstallationId.Trim(),
            RetentionDays = Math.Clamp(source.RetentionDays, 30, 730)
        };
    }

    private static string GetAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString(3)
               ?? "unknown";
    }
}
