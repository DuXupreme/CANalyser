using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Models;

/// <summary>
/// One CAN frame as displayed in the Busmaster-style message window.
/// </summary>
public sealed class BusmasterMessageRow
{
    public BusmasterMessageRow(
        RawCanFrame frame,
        bool isDecoded,
        string? decodedMessageName)
    {
        Source = frame;
        IsDecoded = isDecoded;
        MessageName = string.IsNullOrWhiteSpace(decodedMessageName) ? frame.IdHex : decodedMessageName;
    }

    public RawCanFrame Source { get; }

    public double TimeSeconds => Source.TimeSeconds;

    public long TimestampNanoseconds => Source.TimestampNanoseconds;

    public long FrameIndex => Source.FrameIndex;

    public long SourceLineNumber => Source.SourceLineNumber;

    public string Direction
    {
        get
        {
            return Source.Direction == CanFrameDirection.Transmit ? "Tx" :
                Source.Direction == CanFrameDirection.Receive ? "Rx" : "?";
        }
    }

    public string Channel => string.IsNullOrWhiteSpace(Source.Channel) ? "1" : Source.Channel;

    public string MessageType => $"{(Source.FrameFormat == CanFrameFormat.FlexibleDataRate ? "FD" : "CAN")}/{(Source.IsExtended ? "Ext" : "Std")}";

    public string IdHex => Source.IdHex;

    public string MessageName { get; }

    public int Dlc => Source.Dlc;

    public int PayloadLength => Source.PayloadLength;

    public string DataBytes => Source.DataHex;

    public bool IsDecoded { get; }
}
