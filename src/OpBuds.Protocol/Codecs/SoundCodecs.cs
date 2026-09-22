using OpBuds.Protocol.Model;

namespace OpBuds.Protocol.Codecs;

/// <summary>EQ preset read (<c>0x010F</c>) and write (<c>0x0406</c>).</summary>
/// <remarks>
/// <b>The setter performs no range validation.</b> Every byte <c>0x00</c>-<c>0x07</c> is
/// accepted with an OK ack, a genuine state-change notification, and a readback mirroring the
/// value — including ids the phone never selects and whose audible effect is unconfirmed. A
/// stored out-of-range preset may leave the phone's own UI showing no selection, so the
/// validation has to happen here.
/// </remarks>
public static class EqPresetCodec
{
    public static byte[] BuildRequest() => [];

    public static byte[] BuildSet(EqPreset preset)
    {
        if (!Enum.IsDefined(typeof(EqPreset), preset))
        {
            throw new ArgumentOutOfRangeException(
                nameof(preset),
                preset,
                "the device would accept this and echo it back; refusing to send an unmodelled preset");
        }

        return [(byte)preset];
    }

    /// <summary>Parses <c>[status][preset]</c>.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> payload, out EqPreset preset)
    {
        preset = default;
        if (payload.Length < 2 || payload.Span[0] != CountPrefixed.StatusOk)
        {
            return false;
        }

        var raw = payload.Span[1];
        if (!Enum.IsDefined(typeof(EqPreset), raw))
        {
            return false;
        }

        preset = (EqPreset)raw;
        return true;
    }
}

/// <summary>
/// BassWave: read <c>0x0124</c> as <c>[status][min][max][current]</c>, write <c>0x041B</c>
/// as <c>[min][max][level]</c>. All signed.
/// </summary>
public static class BassWaveCodec
{
    public static byte[] BuildRequest() => [];

    /// <summary>Builds the <c>0x041B</c> payload for <paramref name="level"/>.</summary>
    /// <remarks>
    /// The getter's reply minus its status byte — the rule every confirmed setter on this
    /// hardware follows — and byte for byte the body of the <c>0x0508</c> push. The bounds
    /// are echoed from the device's own reading rather than hardcoded, and a level outside
    /// them is refused here: what the firmware does with one is unknown.
    /// </remarks>
    public static byte[] BuildSet(BassWaveState bounds, int level)
    {
        if (level < bounds.Minimum || level > bounds.Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                $"the earbuds report a range of {bounds.Minimum} to {bounds.Maximum}");
        }

        return
        [
            unchecked((byte)bounds.Minimum),
            unchecked((byte)bounds.Maximum),
            unchecked((byte)level),
        ];
    }

    public static bool TryParse(ReadOnlyMemory<byte> payload, out BassWaveState state)
    {
        state = default;
        if (payload.Length < 4 || payload.Span[0] != CountPrefixed.StatusOk)
        {
            return false;
        }

        var span = payload.Span;
        state = new BassWaveState((sbyte)span[1], (sbyte)span[2], (sbyte)span[3]);
        return true;
    }
}

/// <summary>
/// Custom EQ profiles, <c>0x0122</c>.
/// </summary>
/// <remarks>
/// <c>[status][count]</c> then per profile
/// <c>[slot][minGain][maxGain][unknown][nameLen][name][bandCount]</c> followed by
/// <c>[freq16 LE][signed gain]</c> per band. All six band frequencies were matched exactly
/// against the device: 62, 250, 1000, 4000, 8000, 16000 Hz.
/// </remarks>
public static class CustomEqCodec
{
    /// <summary>The band frequencies this model uses, confirmed byte-for-byte.</summary>
    public static readonly int[] BandFrequenciesHz = [62, 250, 1000, 4000, 8000, 16000];

    public static byte[] BuildRequest() => [];

    public static bool TryParse(ReadOnlyMemory<byte> payload, out IReadOnlyList<CustomEqProfile> profiles)
    {
        profiles = Array.Empty<CustomEqProfile>();

        if (payload.Length < 2 || payload.Span[0] != CountPrefixed.StatusOk)
        {
            return false;
        }

        var span = payload.Span;
        var count = span[1];
        var parsed = new List<CustomEqProfile>(count);
        var offset = 2;

        for (var i = 0; i < count; i++)
        {
            // slot, minGain, maxGain, unknown, nameLen
            if (offset + 5 > span.Length)
            {
                return false;
            }

            var slot = span[offset];
            var minGain = (sbyte)span[offset + 1];
            var maxGain = (sbyte)span[offset + 2];
            var nameLength = span[offset + 4];
            offset += 5;

            if (offset + nameLength + 1 > span.Length)
            {
                return false;
            }

            var name = System.Text.Encoding.ASCII.GetString(span.Slice(offset, nameLength));
            offset += nameLength;

            var bandCount = span[offset++];
            if (offset + (bandCount * 3) > span.Length)
            {
                return false;
            }

            var bands = new EqBand[bandCount];
            for (var b = 0; b < bandCount; b++)
            {
                var at = offset + (b * 3);
                var frequency = span[at] | (span[at + 1] << 8);
                bands[b] = new EqBand(frequency, (sbyte)span[at + 2]);
            }

            offset += bandCount * 3;
            parsed.Add(new CustomEqProfile(slot, name, minGain, maxGain, bands));
        }

        profiles = parsed;
        return true;
    }
}
