namespace OpBuds.Protocol.Framing;

/// <summary>
/// Encodes and decodes single frames.
/// </summary>
/// <remarks>
/// Wire layout:
/// <code>
/// AA | len (varint) | 00 00 | cmdId | msgType | seq | plenLo plenHi | payload
/// </code>
/// <c>len</c> counts everything after the length field itself, so
/// <c>total = 1 + varintBytes + len</c> and <c>len = 7 + payloadLength</c>.
/// </remarks>
public static class FrameCodec
{
    public const byte Magic = 0xAA;

    /// <summary>Bytes between the length field and the payload: <c>00 00 | id | type | seq | plen16</c>.</summary>
    public const int HeaderAfterLength = 7;

    /// <summary>
    /// Largest payload accepted. The biggest frame ever observed is <c>0x011F</c>'s 299-byte
    /// table; this leaves generous headroom while still bounding a desynchronised read.
    /// </summary>
    public const int MaxPayloadLength = 4096;

    /// <summary>Total encoded size of a frame carrying <paramref name="payloadLength"/> bytes.</summary>
    public static int MeasureEncoded(int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        var length = HeaderAfterLength + payloadLength;
        return 1 + Varint.MeasureBytes(length) + length;
    }

    public static byte[] Encode(OpFrame frame)
    {
        var buffer = new byte[MeasureEncoded(frame.Payload.Length)];
        Encode(buffer, frame);
        return buffer;
    }

    /// <summary>Writes <paramref name="frame"/> into <paramref name="destination"/>.</summary>
    /// <returns>Bytes written.</returns>
    public static int Encode(Span<byte> destination, OpFrame frame)
    {
        var payload = frame.Payload.Span;
        var total = MeasureEncoded(payload.Length);
        if (destination.Length < total)
        {
            throw new ArgumentException(
                $"need {total} bytes, got {destination.Length}", nameof(destination));
        }

        var length = HeaderAfterLength + payload.Length;

        destination[0] = Magic;
        var offset = 1 + Varint.Write(destination[1..], length);

        destination[offset++] = 0x00;
        destination[offset++] = 0x00;
        destination[offset++] = frame.CommandId;
        destination[offset++] = frame.MessageType;
        destination[offset++] = frame.Sequence;
        destination[offset++] = (byte)(payload.Length & 0xFF);
        destination[offset++] = (byte)((payload.Length >> 8) & 0xFF);

        payload.CopyTo(destination[offset..]);
        return total;
    }

    /// <summary>
    /// Attempts to decode one frame from the start of <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no checksum anywhere in this protocol. The internal consistency check
    /// <c>len == payloadLength + 7</c> stands in for one: a frame whose declared payload
    /// length disagrees with its length field is rejected as garbage rather than trusted.
    /// It is the only structural validation available.
    /// </para>
    /// </remarks>
    public static DecodeStatus TryDecode(ReadOnlySpan<byte> source, out OpFrame frame, out int consumed)
    {
        frame = default;
        consumed = 0;

        if (source.Length < 1)
        {
            return DecodeStatus.NeedMoreData;
        }

        if (source[0] != Magic)
        {
            return DecodeStatus.Resync;
        }

        switch (Varint.TryRead(source[1..], out var length, out var lengthBytes))
        {
            case VarintStatus.NeedMoreData:
                return DecodeStatus.NeedMoreData;
            case VarintStatus.Malformed:
                return DecodeStatus.Resync;
        }

        if (length < HeaderAfterLength)
        {
            return DecodeStatus.Resync;
        }

        var declaredPayload = length - HeaderAfterLength;
        if (declaredPayload > MaxPayloadLength)
        {
            return DecodeStatus.Resync;
        }

        var headerStart = 1 + lengthBytes;
        var payloadStart = headerStart + HeaderAfterLength;

        // The plen field must be present before the consistency check can run.
        if (source.Length < payloadStart)
        {
            return DecodeStatus.NeedMoreData;
        }

        var statedPayload = source[headerStart + 5] | (source[headerStart + 6] << 8);
        if (statedPayload != declaredPayload)
        {
            return DecodeStatus.Resync;
        }

        var total = headerStart + length;
        if (source.Length < total)
        {
            return DecodeStatus.NeedMoreData;
        }

        frame = new OpFrame(
            CommandId: source[headerStart + 2],
            MessageType: source[headerStart + 3],
            Sequence: source[headerStart + 4],
            Payload: source.Slice(payloadStart, declaredPayload).ToArray());

        consumed = total;
        return DecodeStatus.Ok;
    }
}

public enum DecodeStatus
{
    /// <summary>A frame was decoded and <c>consumed</c> bytes should be dropped.</summary>
    Ok,

    /// <summary>The buffer holds a valid prefix. Append more bytes and retry.</summary>
    NeedMoreData,

    /// <summary>The buffer does not start a valid frame. Skip a byte and hunt for the next magic.</summary>
    Resync,
}
