using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CsvHelper;
using CsvHelper.Configuration;

namespace CanAnalyzer.Core.Analysis;

/// <summary>
/// Imports wide Actuator Testbench telemetry CSVs directly into CANalyser.
/// Every run is aligned to its first STEP target transition at t=0, allowing
/// equivalent traces from multiple files to be overlaid without fake CAN frames.
/// </summary>
public sealed class ActuatorCsvImportService : IActuatorCsvImportService
{
    private static readonly SignalDefinition[] SignalDefinitions =
    [
        new("target_position_pct", "TargetPositionPct", "Target position", "%"),
        new("command_position_pct", "CommandPositionPct", "Command position", "%"),
        new("actual_position_pct", "ActualPositionPct", "Actual position", "%"),
        new("error_pct", "PositionErrorPct", "Position error", "%"),
        new("pwm", "Pwm", "PWM", "PWM"),
        new("current_a", "CurrentA", "Supply current", "A"),
        new("filtered_current_a", "FilteredCurrentA", "Filtered current", "A"),
        new("peak_current_a", "PeakCurrentA", "Peak current", "A"),
        new("bus_voltage_v", "BusVoltageV", "Bus voltage", "V"),
        new("shunt_voltage_mv", "ShuntVoltageMv", "Shunt voltage", "mV"),
        new("power_w", "PowerW", "Power", "W"),
        new("fault_code", "FaultCode", "Fault code", ""),
        new("fault_latched", "FaultLatched", "Fault latched", "bool"),
        new("lower_limit", "LowerLimit", "Lower limit active", "bool"),
        new("upper_limit", "UpperLimit", "Upper limit active", "bool"),
        new("estop", "EmergencyStop", "Emergency stop", "bool")
    ];

    private static readonly string[] RequiredHeaders =
    [
        "arduino_time_ms", "mode", "target_position_pct", "command_position_pct",
        "actual_position_pct", "error_pct", "pwm", "current_a",
        "filtered_current_a", "peak_current_a", "bus_voltage_v",
        "shunt_voltage_mv", "power_w", "fault_code", "fault_latched",
        "lower_limit", "upper_limit", "estop"
    ];

    public Task<CanDataset> ImportAsync(
        IReadOnlyList<string> filePaths,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0)
        {
            throw new ArgumentException("Selecteer minstens één Actuator Testbench CSV.", nameof(filePaths));
        }

        return Task.Run(() => ImportCore(filePaths, progress, cancellationToken), cancellationToken);
    }

    private static CanDataset ImportCore(
        IReadOnlyList<string> filePaths,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var allSeries = new List<SignalSeries>();
        var issues = new List<ImportIssue>();
        var runNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalRows = 0;

        for (var fileIndex = 0; fileIndex < filePaths.Count; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = filePaths[fileIndex];
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Actuator CSV niet gevonden.", path);
            }

            progress?.Report(new LoadProgress(
                $"Actuator-run {fileIndex + 1}/{filePaths.Count} lezen...",
                fileIndex * 80 / filePaths.Count));

            var runName = UniqueRunName(Path.GetFileNameWithoutExtension(path), runNames);
            var rows = ReadRows(path, cancellationToken);
            if (rows.Count < 2)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} bevat minder dan twee meetpunten.");
            }

            totalRows += rows.Count;
            var actualPositions = rows.Select(static row => row.Values["actual_position_pct"]).ToArray();
            if (actualPositions.Max() - actualPositions.Min() < 0.01)
            {
                issues.Add(new ImportIssue(
                    ImportIssueSeverity.Warning,
                    "ACTUATOR_POSITION_CONSTANT",
                    nameof(ActuatorCsvImportService),
                    0,
                    $"{Path.GetFileName(path)}: Actual position is constant ({actualPositions[0]:0.###}%) over all {rows.Count:N0} samples.",
                    string.Empty));
            }
            if (rows.All(static row => Math.Abs(row.Values["pwm"]) < 0.5))
            {
                issues.Add(new ImportIssue(
                    ImportIssueSeverity.Warning,
                    "ACTUATOR_PWM_ZERO",
                    nameof(ActuatorCsvImportService),
                    0,
                    $"{Path.GetFileName(path)}: PWM is zero in every sample; this run contains no commanded motor movement.",
                    string.Empty));
            }
            var alignmentIndex = FindStepTransition(rows);
            if (alignmentIndex < 0)
            {
                alignmentIndex = rows.FindIndex(static row => string.Equals(row.Mode, "STEP", StringComparison.OrdinalIgnoreCase));
                if (alignmentIndex < 0) alignmentIndex = 0;
                issues.Add(new ImportIssue(
                    ImportIssueSeverity.Warning,
                    "ACTUATOR_NO_STEP_TRANSITION",
                    nameof(ActuatorCsvImportService),
                    0,
                    $"{Path.GetFileName(path)} bevat geen STEP-doelovergang; uitgelijnd op de eerste STEP-rij of eerste rij.",
                    string.Empty));
            }

            var alignmentMs = rows[alignmentIndex].ArduinoTimeMs;
            var timestamps = rows
                .Select(row => checked((row.ArduinoTimeMs - alignmentMs) * 1_000_000L))
                .ToArray();

            foreach (var definition in SignalDefinitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = new SignalIdentity(
                    runName,
                    CanFrameFormat.Classic,
                    false,
                    0,
                    "ActuatorTestbench",
                    definition.SignalName);
                var values = rows.Select(row => row.Values[definition.CsvName]).ToArray();
                var label = $"{runName} · {definition.DisplayName} [{definition.Unit}]";
                allSeries.Add(new SignalSeries(identity, timestamps, values, label));
            }
        }

        progress?.Report(new LoadProgress("Vergelijkingsdataset opbouwen...", 90));
        var byLabel = allSeries.ToDictionary(static series => series.Label, StringComparer.Ordinal);
        var byIdentity = allSeries.ToDictionary(static series => series.Identity);
        var sourceHash = ComputeCombinedHash(filePaths, cancellationToken);
        var report = new ImportReport(
            nameof(ActuatorCsvImportService),
            totalRows + filePaths.Count,
            filePaths.Count,
            totalRows,
            0,
            issues,
            ImportMode.Strict);

        progress?.Report(new LoadProgress("Actuator-runs geladen.", 100));
        return new CanDataset
        {
            RawFrames = Array.Empty<RawCanFrame>(),
            DecodedSamples = Array.Empty<DecodedSignalSample>(),
            MessageSummaries = Array.Empty<MessageSummary>(),
            SignalSeriesByLabel = byLabel,
            SignalSeriesByIdentity = byIdentity,
            SignalLabels = allSeries.Select(static series => series.Label).ToArray(),
            Diagnostics = new DecoderDiagnostics(
                0, 0, 0, 0, 0,
                $"Actuator CSV vergelijking: {filePaths.Count} run(s), uitgelijnd op de eerste STEP-doelovergang."),
            ImportReport = report,
            Completeness = DatasetCompleteness.Complete,
            SourceLogSha256 = sourceHash,
            DbcSha256 = "niet-van-toepassing",
            ApplicationVersion = typeof(ActuatorCsvImportService).Assembly.GetName().Version?.ToString() ?? "unknown"
        };
    }

    private static List<ActuatorRow> ReadRows(string path, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            TrimOptions = TrimOptions.Trim
        });
        if (!csv.Read() || !csv.ReadHeader())
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} heeft geen geldige CSV-header.");
        }

        var headers = csv.HeaderRecord?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var missing = RequiredHeaders.Where(header => !headers.Contains(header)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} mist verplichte kolommen: {string.Join(", ", missing)}.");
        }

        var rows = new List<ActuatorRow>();
        while (csv.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRow = csv.Context.Parser?.Row ?? 0;
            var time = ParseLong(csv.GetField("arduino_time_ms"), "arduino_time_ms", sourceRow);
            var mode = csv.GetField("mode") ?? string.Empty;
            var values = SignalDefinitions.ToDictionary(
                static definition => definition.CsvName,
                definition => ParseDouble(csv.GetField(definition.CsvName), definition.CsvName, sourceRow),
                StringComparer.OrdinalIgnoreCase);
            rows.Add(new ActuatorRow(time, mode, values));
        }
        return rows;
    }

    private static int FindStepTransition(IReadOnlyList<ActuatorRow> rows)
    {
        double? previousTarget = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!string.Equals(rows[i].Mode, "STEP", StringComparison.OrdinalIgnoreCase)) continue;
            var target = rows[i].Values["target_position_pct"];
            if (previousTarget.HasValue && Math.Abs(target - previousTarget.Value) > 1e-9) return i;
            previousTarget = target;
        }
        return -1;
    }

    private static string UniqueRunName(string stem, ISet<string> used)
    {
        var baseName = stem.StartsWith("actuator_", StringComparison.OrdinalIgnoreCase)
            ? stem["actuator_".Length..]
            : stem;
        var candidate = baseName;
        for (var suffix = 2; !used.Add(candidate); suffix++) candidate = $"{baseName} ({suffix})";
        return candidate;
    }

    private static long ParseLong(string? text, string column, long row)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        throw new InvalidDataException($"Ongeldig geheel getal in kolom {column}, CSV-regel {row}: '{text}'.");
    }

    private static double ParseDouble(string? text, string column, long row)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) return value;
        throw new InvalidDataException($"Ongeldig getal in kolom {column}, CSV-regel {row}: '{text}'.");
    }

    private static string ComputeCombinedHash(IReadOnlyList<string> filePaths, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            using var stream = File.OpenRead(path);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed record SignalDefinition(string CsvName, string SignalName, string DisplayName, string Unit);
    private sealed record ActuatorRow(long ArduinoTimeMs, string Mode, Dictionary<string, double> Values);
}
