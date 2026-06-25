using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Models;

/// <summary>
/// UI row wrapper for raw frame table.
/// </summary>
public sealed class RawFrameRow
{
    public RawFrameRow(RawCanFrame frame)
    {
        Source = frame;
    }

    public RawCanFrame Source { get; }

    public double TimeSeconds => Source.TimeSeconds;

    public long TimestampNanoseconds => Source.TimestampNanoseconds;

    public long FrameIndex => Source.FrameIndex;

    public long SourceLineNumber => Source.SourceLineNumber;

    public string Type => Source.Type;

    public string Channel => Source.Channel;

    public string IdHex => Source.IdHex;

    public uint Id => Source.Id;

    public int Dlc => Source.Dlc;

    public int PayloadLength => Source.PayloadLength;

    public string FrameFormat => Source.FrameFormat == CanFrameFormat.FlexibleDataRate ? "FD" : "Classic";

    public string Direction => Source.Direction.ToString();

    public string DataHex => Source.DataHex;

    public string DataAscii => Source.DataAscii;
}
