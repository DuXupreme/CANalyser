namespace CanAnalyzer.App.Models;

public sealed record OnlineMachine(string Name, string LoggerId, string MachineId);

/// <summary>Shared names for the online selector and the loaded dataset's provenance.</summary>
public static class OnlineMachineCatalog
{
    // Physical handover confirmed on 15 October 2026 at 13:31:19 Europe/Amsterdam.
    // Assignment follows measurement time, including delayed uploads and cached files.
    public static readonly DateTimeOffset? Machine3TransferUtc = new(2026, 10, 15, 11, 31, 19, TimeSpan.Zero);

    public static IReadOnlyList<OnlineMachine> Machines { get; } =
    [
        new("Vlindermachine 1", "48EDFD35", "48EDFD35"),
        new("Vlindermachine 2", "22484AAA", "22484AAA"),
        new("Vlindermachine 3", "48EDFD35", "machine-3")
    ];

    public static string ResolveName(string? loggerId, DateTimeOffset? recordedAt = null) =>
        ResolveName(loggerId, recordedAt, Machine3TransferUtc);

    public static string ResolveName(CanAnalyzer.Core.Domain.SourceLogFile source)
    {
        if (HasInstallationIdentity(source))
            return source.MachineId switch
            {
                "M01" => "Vlindermachine 1", "M02" => "Vlindermachine 2", "M03" => "Vlindermachine 3",
                _ => source.MachineId ?? "Onbekend"
            };
        if (string.Equals(source.Logger, "48EDFD35", StringComparison.OrdinalIgnoreCase) && !HasClockEvidence(source))
            return "Onbekend (machine 1/3; loggerklok niet bevestigd)";
        return ResolveName(source.Logger, source.RecordedAt);
    }

    public static string ResolveName(string? loggerId, DateTimeOffset? recordedAt, DateTimeOffset? transferUtc)
    {
        if (string.Equals(loggerId, "48EDFD35", StringComparison.OrdinalIgnoreCase))
            return transferUtc is null ? "Vlindermachine 1"
                : recordedAt is null ? "Onbekend (logger 48EDFD35)"
                : recordedAt < transferUtc ? "Vlindermachine 1" : "Vlindermachine 3";
        return Machines.FirstOrDefault(machine => string.Equals(machine.LoggerId, loggerId, StringComparison.OrdinalIgnoreCase))?.Name
               ?? "Onbekend";
    }

    public static string ResolveExportMachine(IEnumerable<CanAnalyzer.Core.Domain.SourceLogFile> sources) =>
        ResolveExportMachine(sources, Machine3TransferUtc);

    public static string ResolveExportMachine(IEnumerable<CanAnalyzer.Core.Domain.SourceLogFile> sources, DateTimeOffset? transferUtc)
    {
        var names = sources.Select(source => HasInstallationIdentity(source) || !HasClockEvidence(source)
            ? ResolveName(source) : ResolveName(source.Logger, source.RecordedAt, transferUtc)).Distinct(StringComparer.Ordinal).ToArray();
        return names is [var name] && !name.StartsWith("Onbekend", StringComparison.Ordinal) ? name : "";
    }

    private static bool HasInstallationIdentity(CanAnalyzer.Core.Domain.SourceLogFile source) =>
        !string.IsNullOrWhiteSpace(source.MachineId) && !string.IsNullOrWhiteSpace(source.InstallationId) &&
        (source.MachineAssignmentQuality == "embedded-installation" ||
         source.MachineAssignmentQuality == "verified-installation" && !string.IsNullOrWhiteSpace(source.MachineAssignmentEvidence));

    private static bool HasClockEvidence(CanAnalyzer.Core.Domain.SourceLogFile source) =>
        source.ClockQuality == "verified" && !string.IsNullOrWhiteSpace(source.ClockEvidence);
}
