namespace CanAnalyzer.Core.Decoding;

/// <summary>
/// Computes which payload bits (LSB0 numbering) a DBC signal occupies.
/// Shared by the decoder and the database editor so the visual bit-layout and the
/// actual decode always agree.
/// </summary>
public static class DbcBitLayout
{
    /// <summary>
    /// Returns the payload bit indices (LSB0: bit 0 = byte 0, bit 0) covered by a signal.
    /// Little-endian (Intel) signals occupy a contiguous range starting at <paramref name="startBit"/>;
    /// big-endian (Motorola) signals follow the classic byte-boundary walk.
    /// </summary>
    public static IReadOnlyList<int> GetOccupiedLsb0Bits(int startBit, int length, bool isLittleEndian)
    {
        if (length <= 0 || startBit < 0)
        {
            return [];
        }

        var bits = new List<int>(length);

        if (isLittleEndian)
        {
            for (var i = 0; i < length; i++)
            {
                bits.Add(startBit + i);
            }

            return bits;
        }

        var bit = startBit;
        for (var i = 0; i < length; i++)
        {
            bits.Add(bit);
            if (bit % 8 == 0)
            {
                bit += 15;
            }
            else
            {
                bit -= 1;
            }
        }

        return bits;
    }

    /// <summary>
    /// Returns whether two signals can be present in the same decoded payload.
    /// Signals on disjoint multiplexer values or ranges may reuse payload bits safely.
    /// </summary>
    public static bool CanBeActiveTogether(
        bool leftIsMultiplexer,
        IReadOnlyList<int> leftMultiplexerIds,
        IReadOnlyList<DbcMultiplexerRange> leftMultiplexerRanges,
        bool rightIsMultiplexer,
        IReadOnlyList<int> rightMultiplexerIds,
        IReadOnlyList<DbcMultiplexerRange> rightMultiplexerRanges)
    {
        if (leftIsMultiplexer || rightIsMultiplexer)
        {
            return true;
        }

        var leftIntervals = GetMuxIntervals(leftMultiplexerIds, leftMultiplexerRanges);
        var rightIntervals = GetMuxIntervals(rightMultiplexerIds, rightMultiplexerRanges);
        if (leftIntervals.Count == 0 || rightIntervals.Count == 0)
        {
            return true;
        }

        return leftIntervals.Any(leftInterval =>
            rightIntervals.Any(rightInterval =>
                leftInterval.Minimum <= rightInterval.Maximum &&
                rightInterval.Minimum <= leftInterval.Maximum));
    }

    private static IReadOnlyList<(uint Minimum, uint Maximum)> GetMuxIntervals(
        IReadOnlyList<int> multiplexerIds,
        IReadOnlyList<DbcMultiplexerRange> multiplexerRanges)
    {
        if (multiplexerRanges.Count > 0)
        {
            return multiplexerRanges
                .Select(static range => (range.Minimum, range.Maximum))
                .ToList();
        }

        return multiplexerIds
            .Where(static id => id >= 0)
            .Select(static id => ((uint)id, (uint)id))
            .ToList();
    }
}
