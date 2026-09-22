using OpBuds.Protocol.Model;

namespace OpBuds.Protocol.Codecs;

/// <summary>
/// A parsed touch configuration, retaining the exact bytes it was decoded from.
/// </summary>
/// <remarks>
/// <see cref="RawPayload"/> exists so a write can be checked byte for byte: after re-pointing
/// one gesture, the table read back must equal this one with exactly that byte changed.
/// Comparing models instead would miss a change to any field this decoder does not yet
/// understand.
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
    /// Builds the <c>0x0401</c> payload that re-points one gesture:
    /// <c>[count=1][side][type][gesture][action]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One record of the <c>0x0108</c> reply behind a count — the getter's reply minus its
    /// status byte, cut down to the record being changed. That is the same shape as the
    /// confirmed ANC setter, and the same encoding Gadgetbridge uses for the Oppo family.
    /// </para>
    /// <para>
    /// Side, context and gesture must come from a binding that was read from the device; only
    /// the action is new. So a write can re-point a gesture the firmware reported, and can
    /// never invent one — and because nothing else is sent, nothing else can be normalised.
    /// </para>
    /// </remarks>
    public static byte[] BuildSet(TouchConfiguration current, TouchBinding binding, byte action)
    {
        if (!current.Bindings.Contains(binding))
        {
            throw new ArgumentException(
                $"no record for side {binding.Side}, type 0x{binding.ContextType:X2}, " +
                $"gesture 0x{binding.Gesture:X2} was read from the earbuds",
                nameof(binding));
        }

        return [0x01, (byte)binding.Side, binding.ContextType, binding.Gesture, action];
    }

    /// <summary>
    /// True when <paramref name="after"/> is <paramref name="before"/> with exactly one byte
    /// different: <paramref name="binding"/>'s action, now <paramref name="action"/>.
    /// </summary>
    /// <remarks>
    /// Compared on the raw bytes, not the model, so a change to a field this decoder does
    /// not understand still counts as a change.
    /// </remarks>
    public static bool IsExactlyOneEdit(
        TouchConfiguration before,
        TouchConfiguration after,
        TouchBinding binding,
        byte action)
    {
        var expected = before.RawPayload.ToArray();
        var edited = false;

        for (var offset = 2; offset + RecordWidth <= expected.Length; offset += RecordWidth)
        {
            if (expected[offset] == (byte)binding.Side
                && expected[offset + 1] == binding.ContextType
                && expected[offset + 2] == binding.Gesture)
            {
                expected[offset + 3] = action;
                edited = true;
                break;
            }
        }

        return edited && after.RawPayload.Span.SequenceEqual(expected);
    }
}
