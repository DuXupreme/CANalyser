using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Models;

/// <summary>
/// One CAN frame as displayed in the Busmaster-style message window.
/// </summary>
public sealed class BusmasterMessageRow
{
    public BusmasterMessageRow(
        RawCanFrame frame,
        IReadOnlyList<DecodedSignalSample> decodedSignals)
    {
        Source = frame;
        DecodedSignals = decodedSignals;
        MessageName = decodedSignals.FirstOrDefault()?.MessageName ?? frame.IdHex;
    }

    public RawCanFrame Source { get; }

    public IReadOnlyList<DecodedSignalSample> DecodedSignals { get; }

    public double TimeSeconds => Source.TimeSeconds;

    public string Direction
    {
        get
        {
            if (Source.Type.Contains("tx", StringComparison.OrdinalIgnoreCase))
            {
                return "Tx";
            }

            if (Source.Type.Contains("rx", StringComparison.OrdinalIgnoreCase))
            {
                return "Rx";
            }

            return "Rx";
        }
    }

    public string Channel => string.IsNullOrWhiteSpace(Source.Channel) ? "1" : Source.Channel;

    public string MessageType => Source.IsExtended ? "x" : "s";

    public string IdHex => Source.IdHex;

    public string MessageName { get; }

    public int Dlc => Source.Dlc;

    public string DataBytes => Source.DataHex;

    public bool IsDecoded => DecodedSignals.Count > 0;
}
