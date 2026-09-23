namespace CanAnalyzer.App.Models;

public sealed record OnlineMachine(string Name, string LoggerId);

/// <summary>Shared names for the online selector and the loaded dataset's provenance.</summary>
public static class OnlineMachineCatalog
{
    public static IReadOnlyList<OnlineMachine> Machines { get; } =
    [
        new("Vlindermachine 1", "48EDFD35"),
        new("Vlindermachine 2", "22484AAA")
    ];

    public static string ResolveName(string? loggerId) =>
        Machines.FirstOrDefault(machine => string.Equals(machine.LoggerId, loggerId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? "Onbekend";
}
