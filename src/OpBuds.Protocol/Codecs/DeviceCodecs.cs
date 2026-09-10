using System.Text;
using OpBuds.Protocol.Model;

namespace OpBuds.Protocol.Codecs;

/// <summary>Battery, <c>0x0106</c> and notification topic <c>0x01</c>.</summary>
/// <remarks>
/// <c>percent = raw &amp; 0x7F</c>. The case (id 3) appears in the list only when present:
/// a battery notification gained a third entry 0.01 s after a wear notification showed the
/// right earbud entering the case.
/// </remarks>
public static class BatteryCodec
{
    public static byte[] BuildRequest() => [];

    public static bool TryParse(ReadOnlyMemory<byte> payload, out IReadOnlyList<BatteryLevel> levels)
    {
        levels = Array.Empty<BatteryLevel>();

        if (CountPrefixed.TryParse(payload, recordWidth: 2, out var records) != ParseStatus.Ok)
        {
            return false;
        }

        levels = records
            .Select(record => new BatteryLevel(
                (BudSide)record.Id,
                record.Value & 0x7F,
                (record.Value & BatteryLevel.ChargingBit) != 0))
            .ToArray();

        return true;
    }
}

/// <summary>Wear / in-ear state, <c>0x0109</c> and notification topic <c>0x02</c>.</summary>
public static class WearCodec
{
    public static byte[] BuildRequest() => [];

    public static bool TryParse(ReadOnlyMemory<byte> payload, out IReadOnlyList<WearState> states)
    {
        states = Array.Empty<WearState>();

        if (CountPrefixed.TryParse(payload, recordWidth: 2, out var records) != ParseStatus.Ok)
        {
            return false;
        }

        states = records
            .Select(record => new WearState((BudSide)record.Id, record.Value))
            .ToArray();

        return true;
    }
}

/// <summary>Firmware, <c>0x0105</c>. The payload is ASCII, not binary.</summary>
/// <remarks>
/// Reads as comma-separated fields, e.g. <c>1,2,138,2,2,138,3,1,01,3,2,105</c>. The 4x3
/// grouping is the leading interpretation but is not forced by any length field, so this
/// codec exposes the raw string and leaves grouping to the caller. <c>likely</c>
/// </remarks>
public static class FirmwareCodec
{
    public static byte[] BuildRequest() => [];

    public static bool TryParse(ReadOnlyMemory<byte> payload, out string firmware)
    {
        firmware = string.Empty;
        if (payload.Length < 2 || payload.Span[0] != CountPrefixed.StatusOk)
        {
            return false;
        }

        firmware = Encoding.ASCII.GetString(payload.Span[2..]).TrimEnd('\0');
        return true;
    }
}

/// <summary>
/// Connected peers, <c>0x0112</c> and notification topic <c>0x06</c>.
/// </summary>
/// <remarks>
/// <c>[status][count]</c> then per record
/// <c>[MAC 6B byte-reversed][attr][0x02][flag][nameLen][name]</c>. Every record carries an
/// explicit name length, so a nameless peer is simply length <c>0x00</c> — there is no
/// optional-field ambiguity.
/// </remarks>
public static class ConnectedDevicesCodec
{
    public static byte[] BuildRequest() => [];

    public static bool TryParse(ReadOnlyMemory<byte> payload, out IReadOnlyList<ConnectedDevice> devices)
    {
        devices = Array.Empty<ConnectedDevice>();

        if (payload.Length < 2 || payload.Span[0] != CountPrefixed.StatusOk)
        {
            return false;
        }

        var span = payload.Span;
        var count = span[1];
        var parsed = new List<ConnectedDevice>(count);
        var offset = 2;

        for (var i = 0; i < count; i++)
        {
            // 6 MAC + attr + constant 0x02 + flag + nameLen
            if (offset + 10 > span.Length)
            {
                return false;
            }

            var mac = span.Slice(offset, 6);
            var attribute = span[offset + 6];
            var flag = span[offset + 8];
            var nameLength = span[offset + 9];
            offset += 10;

            if (offset + nameLength > span.Length)
            {
                return false;
            }

            var name = Encoding.UTF8.GetString(span.Slice(offset, nameLength));
            offset += nameLength;

            parsed.Add(new ConnectedDevice(FormatReversedMac(mac), name, attribute, flag));
        }

        devices = parsed;
        return true;
    }

    /// <summary>The MAC arrives byte-reversed, so <c>5f4e3d2c1b0a</c> is <c>0A:1B:2C:3D:4E:5F</c>.</summary>
    private static string FormatReversedMac(ReadOnlySpan<byte> raw)
    {
        var parts = new string[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            parts[i] = raw[raw.Length - 1 - i].ToString("X2");
        }

        return string.Join(':', parts);
    }
}

/// <summary>
/// Find device, <c>0x0400</c>. Payload is a count-prefixed id list.
/// </summary>
/// <remarks>
/// <b>There is no stop opcode.</b> The tone self-terminates after a few seconds and none of
/// the plausible stop candidates did anything, so a Find button must be fire-and-forget and
/// must not offer a Stop control it cannot honour.
/// </remarks>
public static class FindDeviceCodec
{
    public static byte[] BuildSet(FindTarget target)
    {
        if (target == FindTarget.None)
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "nothing to ring");
        }

        var ids = new List<byte>(2);
        if (target.HasFlag(FindTarget.Left))
        {
            ids.Add((byte)BudSide.Left);
        }

        if (target.HasFlag(FindTarget.Right))
        {
            ids.Add((byte)BudSide.Right);
        }

        return CountPrefixed.BuildRequest([.. ids]);
    }
}
