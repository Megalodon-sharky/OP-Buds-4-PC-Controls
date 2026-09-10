using OpBuds.Protocol.Model;

namespace OpBuds.Protocol.Codecs;

/// <summary>
/// ANC read (<c>0x010C</c>) and write (<c>0x0404</c>).
/// </summary>
/// <remarks>
/// <para>
/// The set value is <b>16-bit little-endian and mirrors the readback mask exactly</b>. This
/// cost a full session to find: every write had been three bytes long, and a 3-byte payload
/// cannot express a value living in the high byte, so Adaptive (<c>0x0800</c>) looked like a
/// missing feature rather than a truncated argument.
/// </para>
/// <para>
/// The field also accepts short convenience selectors — <c>0x01</c> off, <c>0x02</c> ANC at
/// high, <c>0x04</c> transparency — which the firmware expands. This codec never emits them:
/// the explicit 16-bit form is unambiguous and is the only form that can reach every state.
/// </para>
/// </remarks>
public static class AncCodec
{
    /// <summary>The only valid field id. Ids <c>0x02</c>-<c>0x06</c> return the error shape.</summary>
    public const byte FieldId = 0x01;

    private const int RecordWidth = 3;

    /// <summary>Request payload for <c>0x010C</c>.</summary>
    public static byte[] BuildRequest() => CountPrefixed.BuildRequest(FieldId);

    /// <summary>Set payload for <c>0x0404</c>: <c>[count][field][valueLo][valueHi]</c>.</summary>
    public static byte[] BuildSet(AncMode mode)
    {
        if (!IsKnown(mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode), mode, "not a confirmed ANC mask; refusing to transmit it");
        }

        var value = (ushort)mode;
        return [0x01, FieldId, (byte)(value & 0xFF), (byte)(value >> 8)];
    }

    /// <summary>Parses a <c>0x810C</c> reply or a <c>0x0204</c> topic-3 notification body.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> payload, out AncMode mode)
    {
        mode = default;

        if (CountPrefixed.TryParse(payload, RecordWidth, out var records) != ParseStatus.Ok)
        {
            return false;
        }

        foreach (var record in records)
        {
            if (record.Id != FieldId || record.Data.Length < 2)
            {
                continue;
            }

            var raw = (ushort)(record.Data.Span[0] | (record.Data.Span[1] << 8));
            if (!Enum.IsDefined(typeof(AncMode), raw))
            {
                return false;
            }

            mode = (AncMode)raw;
            return true;
        }

        return false;
    }

    public static bool IsKnown(AncMode mode) => Enum.IsDefined(typeof(AncMode), mode);
}
