namespace OpBuds.Protocol.Codecs;

/// <summary>One record from a count-prefixed reply. <see cref="Data"/> excludes the id byte.</summary>
public readonly record struct CountPrefixedRecord(byte Id, ReadOnlyMemory<byte> Data)
{
    /// <summary>First data byte, for the common one-byte-value case.</summary>
    public byte Value => Data.Length == 0 ? (byte)0 : Data.Span[0];
}

public enum ParseStatus
{
    Ok,

    /// <summary>Payload was shorter than the two-byte header.</summary>
    TooShort,

    /// <summary>Status byte was non-zero. <c>0x02</c> means the arguments were rejected.</summary>
    DeviceError,

    /// <summary>The declared record count does not fit in the payload.</summary>
    LengthMismatch,
}

/// <summary>
/// The central structure of this protocol: a request is <c>[count][id...]</c> and the reply is
/// <c>[status][count]</c> followed by fixed-width records that each begin with an id.
/// </summary>
/// <remarks>
/// <para>
/// Unsupported ids are silently dropped from the reply rather than erroring, which makes a
/// single round trip double as capability discovery.
/// </para>
/// <para>
/// <b>That same silence is a trap.</b> At least one handler (<c>0x010D</c>) also truncates
/// its reply at a fixed byte cap and reports the truncated count as though it were the real
/// one. A dropped id is therefore indistinguishable from a truncated one by inspecting the
/// reply alone — callers must compare what came back against what they asked for. See
/// <see cref="MiscConfigCodec"/>.
/// </para>
/// </remarks>
public static class CountPrefixed
{
    public const byte StatusOk = 0x00;

    /// <summary>Status returned when the device rejects the arguments.</summary>
    public const byte StatusRejected = 0x02;

    /// <summary>Builds a <c>[count][id...]</c> request payload.</summary>
    public static byte[] BuildRequest(params byte[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Length > byte.MaxValue)
        {
            throw new ArgumentException("too many ids for a one-byte count", nameof(ids));
        }

        var payload = new byte[ids.Length + 1];
        payload[0] = (byte)ids.Length;
        ids.CopyTo(payload, 1);
        return payload;
    }

    /// <summary>
    /// Parses a <c>[status][count]</c> reply into fixed-width records.
    /// </summary>
    /// <param name="payload">The reply payload, status byte first.</param>
    /// <param name="recordWidth">Record size <b>including</b> the leading id byte.</param>
    /// <param name="records">Decoded records, empty unless the result is <see cref="ParseStatus.Ok"/>.</param>
    public static ParseStatus TryParse(
        ReadOnlyMemory<byte> payload,
        int recordWidth,
        out IReadOnlyList<CountPrefixedRecord> records)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordWidth, 1);
        records = Array.Empty<CountPrefixedRecord>();

        if (payload.Length < 2)
        {
            return ParseStatus.TooShort;
        }

        var span = payload.Span;
        if (span[0] != StatusOk)
        {
            return ParseStatus.DeviceError;
        }

        var count = span[1];
        if (2 + (count * recordWidth) > payload.Length)
        {
            return ParseStatus.LengthMismatch;
        }

        var parsed = new CountPrefixedRecord[count];
        for (var i = 0; i < count; i++)
        {
            var offset = 2 + (i * recordWidth);
            parsed[i] = new CountPrefixedRecord(
                span[offset],
                payload.Slice(offset + 1, recordWidth - 1));
        }

        records = parsed;
        return ParseStatus.Ok;
    }
}
