namespace CanAnalyzer.Core.Domain;

/// <summary>
/// One decoded signal value at a timestamp.
/// </summary>
public sealed record DecodedSignalSample(
    float TimeSeconds,
    uint FrameId,
    string MessageName,
    string SignalName,
    float Value)
{
    public string Label => $"{MessageName}.{SignalName} [0x{FrameId:X}]";
}
