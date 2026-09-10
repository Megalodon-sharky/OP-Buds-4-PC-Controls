namespace OpBuds.Protocol.Framing;

/// <summary>
/// Base-128 (LEB128) varint, as used by the frame length field.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most commonly mis-implemented part of this protocol. Every published
/// implementation treats the length as one byte, which works only because frames are almost
/// always short. A real 309-byte frame carries <c>b2 02</c> there, and a byte-wise reader
/// desynchronises on it permanently.
/// </para>
/// <para>
/// Measured on hardware: <c>0x011F</c> replies with a 299-byte payload in a 309-byte frame
/// whose length field is <c>b2 02</c> = 306.
/// </para>
/// </remarks>
public static class Varint
{
    /// <summary>Longest encoding accepted. Frames are bounded well below this.</summary>
    public const int MaxBytes = 4;

    /// <summary>
    /// Reads a varint from the start of <paramref name="source"/>.
    /// </summary>
    /// <returns>
    /// <see cref="VarintStatus.Ok"/> with <paramref name="value"/> and
    /// <paramref name="bytesRead"/> set; <see cref="VarintStatus.NeedMoreData"/> when the
    /// encoding is truncated; <see cref="VarintStatus.Malformed"/> when it runs past
    /// <see cref="MaxBytes"/>.
    /// </returns>
    public static VarintStatus TryRead(ReadOnlySpan<byte> source, out int value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;

        for (var i = 0; i < MaxBytes; i++)
        {
            if (i >= source.Length)
            {
                return VarintStatus.NeedMoreData;
            }

            var b = source[i];
            value |= (b & 0x7F) << (7 * i);

            if ((b & 0x80) == 0)
            {
                bytesRead = i + 1;
                return VarintStatus.Ok;
            }
        }

        value = 0;
        return VarintStatus.Malformed;
    }

    /// <summary>Writes <paramref name="value"/> and returns the number of bytes written.</summary>
    public static int Write(Span<byte> destination, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        var written = 0;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            destination[written++] = b;
        }
        while (value != 0);

        return written;
    }

    /// <summary>Number of bytes <see cref="Write"/> would produce for <paramref name="value"/>.</summary>
    public static int MeasureBytes(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        var count = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            count++;
        }

        return count;
    }
}

public enum VarintStatus
{
    Ok,
    NeedMoreData,
    Malformed,
}
