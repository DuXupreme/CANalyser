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

    public string Type => Source.Type;

    public string Channel => Source.Channel;

    public string IdHex => Source.IdHex;

    public uint Id => Source.Id;

    public int Dlc => Source.Dlc;

    public string DataHex => Source.DataHex;

    public string DataAscii => Source.DataAscii;
}
