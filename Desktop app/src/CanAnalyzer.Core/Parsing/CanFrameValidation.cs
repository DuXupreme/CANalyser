using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Parsing;

internal static class CanFrameValidation
{
    private static readonly int[] FdPayloadLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    public static bool TryNormalize(
        uint id,
        bool isExtended,
        int declaredDlcOrLength,
        int payloadLength,
        bool fdMarker,
        out byte dlcCode,
        out CanFrameFormat format,
        out string error)
    {
        dlcCode = 0;
        format = CanFrameFormat.Classic;
        error = string.Empty;

        if (id > 0x1FFFFFFF || (!isExtended && id > 0x7FF))
        {
            error = $"CAN-ID 0x{id:X} is outside the {(isExtended ? "29" : "11")}-bit range.";
            return false;
        }

        if (payloadLength is < 0 or > 64)
        {
            error = $"Payload length {payloadLength} is outside 0..64 bytes.";
            return false;
        }

        format = fdMarker || payloadLength > 8 || declaredDlcOrLength > 8
            ? CanFrameFormat.FlexibleDataRate
            : CanFrameFormat.Classic;

        if (format == CanFrameFormat.Classic)
        {
            if (declaredDlcOrLength is < 0 or > 8 || payloadLength != declaredDlcOrLength)
            {
                error = $"Classic CAN DLC {declaredDlcOrLength} does not match payload length {payloadLength}.";
                return false;
            }

            dlcCode = (byte)declaredDlcOrLength;
            return true;
        }

        var codeFromPayload = Array.IndexOf(FdPayloadLengths, payloadLength);
        if (codeFromPayload < 0)
        {
            error = $"CAN FD payload length {payloadLength} is not representable by a DLC code.";
            return false;
        }

        if (declaredDlcOrLength is >= 0 and <= 15)
        {
            var declaredPayload = FdPayloadLengths[declaredDlcOrLength];
            if (declaredPayload != payloadLength && declaredDlcOrLength != payloadLength)
            {
                error = $"CAN FD DLC {declaredDlcOrLength} does not match payload length {payloadLength}.";
                return false;
            }
        }
        else if (declaredDlcOrLength != payloadLength)
        {
            error = $"CAN FD declared length {declaredDlcOrLength} does not match payload length {payloadLength}.";
            return false;
        }

        dlcCode = (byte)codeFromPayload;
        return true;
    }

    public static CanFrameDirection ParseDirection(string value) =>
        value.Contains("tx", StringComparison.OrdinalIgnoreCase)
            ? CanFrameDirection.Transmit
            : value.Contains("rx", StringComparison.OrdinalIgnoreCase)
                ? CanFrameDirection.Receive
                : CanFrameDirection.Unknown;
}
