using OpBuds.Protocol.Model;

namespace OpBuds.Protocol.Codecs;

/// <summary>
/// A parsed touch configuration, retaining the exact bytes it was decoded from.
/// </summary>
/// <remarks>
/// <see cref="RawPayload"/> exists so a write-back can replay the original bytes verbatim
/// with only the intended record edited. Reconstructing the whole table from the model would
/// silently normalise any field this decoder does not yet understand.
/// </remarks>
public sealed record TouchConfiguration(
    IReadOnlyList<TouchBinding> Bindings,
    ReadOnlyMemory<byte> RawPayload);

/// <summary>Touch gesture map, <c>0x0108</c>.</summary>
/// <remarks>
/// <c>[status][count]</c> then count records of <c>[side][type][gesture][action]</c>. This
/// unit reports 18 records in exactly 74 bytes. <b>The request payload is ignored</b> —
/// three different request payloads returned byte-identical replies — so send it empty.
/// </remarks>
public static class TouchCodec
{
    private const int RecordWidth = 4;

    public static byte[] BuildRequest() => [];

    public static bool TryParse(ReadOnlyMemory<byte> payload, out TouchConfiguration configuration)
    {
        configuration = new TouchConfiguration([], ReadOnlyMemory<byte>.Empty);

        if (CountPrefixed.TryParse(payload, RecordWidth, out var records) != ParseStatus.Ok)
        {
            return false;
        }

        var bindings = records
            .Select(record => new TouchBinding(
                (BudSide)record.Id,
                record.Data.Span[0],
                record.Data.Span[1],
                record.Data.Span[2]))
            .ToArray();

        configuration = new TouchConfiguration(bindings, payload);
        return true;
    }

    /// <summary>
    /// Produces a write payload by editing one record inside the bytes that were read.
    /// </summary>
    /// <remarks>
    /// Deliberately takes the original payload rather than a model: everything not being
    /// changed is copied through untouched.
    /// </remarks>
    public static byte[] BuildSetFrom(
        TouchConfiguration original,
        BudSide side,
        byte contextType,
        byte gesture,
        byte action)
    {
        var raw = original.RawPayload.ToArray();

        for (var offset = 2; offset + RecordWidth <= raw.Length; offset += RecordWidth)
        {
            if (raw[offset] == (byte)side && raw[offset + 1] == contextType && raw[offset + 2] == gesture)
            {
                raw[offset + 3] = action;
                return raw;
            }
        }

        throw new ArgumentException(
            $"no record for side {side}, type 0x{contextType:X2}, gesture 0x{gesture:X2}",
            nameof(gesture));
    }
}
