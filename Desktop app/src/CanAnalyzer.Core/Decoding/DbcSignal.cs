namespace CanAnalyzer.Core.Decoding;

public enum DbcSignalValueKind
{
    Integer,
    IeeeFloat32,
    IeeeFloat64
}

public sealed record DbcMultiplexerRange(string MultiplexerSignalName, uint Minimum, uint Maximum);

/// <summary>
/// Parsed DBC signal definition.
/// </summary>
public sealed class DbcSignal
{
    public required string Name { get; init; }

    public required int StartBit { get; init; }

    public required int Length { get; init; }

    public required bool IsLittleEndian { get; init; }

    public required bool IsSigned { get; init; }

    public required double Scale { get; init; }

    public required double Offset { get; init; }

    public required double Minimum { get; init; }

    public required double Maximum { get; init; }

    public required string Unit { get; init; }

    public bool IsMultiplexer { get; init; }

    public IReadOnlyList<int> MultiplexerIds { get; init; } = [];

    public IReadOnlyList<DbcMultiplexerRange> MultiplexerRanges { get; init; } = [];

    public DbcSignalValueKind ValueKind { get; init; } = DbcSignalValueKind.Integer;
}
