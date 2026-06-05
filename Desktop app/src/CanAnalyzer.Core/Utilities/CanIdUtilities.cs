namespace CanAnalyzer.Core.Utilities;

/// <summary>
/// CAN and DBC ID normalization helpers.
/// </summary>
public static class CanIdUtilities
{
    public const uint DbcExtendedFlag = 0x80000000;
    public const uint CanExtendedMask = 0x1FFFFFFF;

    public static uint NormalizeDbcFrameId(uint frameId, bool? isExtended = null)
    {
        var ext = isExtended ?? ((frameId & DbcExtendedFlag) != 0 || frameId > CanExtendedMask);
        if (ext)
        {
            return frameId & CanExtendedMask;
        }

        return frameId <= 0x7FF ? frameId & 0x7FF : frameId;
    }

    public static uint? ExtractJ1939Pgn(uint frameId)
    {
        frameId &= CanExtendedMask;
        if (frameId <= 0x7FF)
        {
            return null;
        }

        var pf = (frameId >> 16) & 0xFF;
        var ps = (frameId >> 8) & 0xFF;
        var dp = (frameId >> 24) & 0x01;

        if (pf < 240)
        {
            return (dp << 16) | (pf << 8);
        }

        return (dp << 16) | (pf << 8) | ps;
    }
}
