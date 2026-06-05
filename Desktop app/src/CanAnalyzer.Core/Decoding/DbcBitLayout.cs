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
}
