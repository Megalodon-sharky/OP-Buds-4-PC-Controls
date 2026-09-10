namespace OpBuds.Protocol.Tests;

/// <summary>
/// Bytes observed on real hardware.
/// </summary>
/// <remarks>
/// Anything not marked <c>SYNTHETIC</c> was captured from a OnePlus Buds 4 and is recorded in
/// <c>docs/protocol.md</c>. Synthetic fixtures follow a documented layout but were not
/// themselves captured, so a test that fails on one is evidence about this code, not about
/// the device.
/// </remarks>
internal static class Fixtures
{
    public static byte[] Hex(string hex) =>
        Convert.FromHexString(hex.Replace(" ", string.Empty).Replace("\n", string.Empty));

    /// <summary>The classic HELLO. Ten bytes on the wire; the frame it declares is nine.</summary>
    public static readonly byte[] Hello = Hex("aa 07 00 00 00 01 23 00 00 12");

    /// <summary>ANC reply, adaptive. <c>[status][count][field][maskLo][maskHi]</c>.</summary>
    public static readonly byte[] AncAdaptive = Hex("00 01 01 00 08");

    /// <summary>ANC reply, transparency.</summary>
    public static readonly byte[] AncTransparency = Hex("00 01 01 00 01");

    /// <summary>Error shape. Status 0x02 means the arguments were rejected.</summary>
    public static readonly byte[] ErrorShape = Hex("02 18 0c 20 01");

    /// <summary>EQ preset reply: currently Bass.</summary>
    public static readonly byte[] EqPresetBass = Hex("00 02");

    /// <summary>BassWave: min -5, max +5, current +5.</summary>
    public static readonly byte[] BassWave = Hex("00 fb 05 05");

    /// <summary>Battery with both buds at 90% and no case present.</summary>
    public static readonly byte[] BatteryBothNinety = Hex("00 02 01 5a 02 5a");

    /// <summary>Wear: both in ear, case present and closed.</summary>
    public static readonly byte[] WearBothInEar = Hex("00 03 01 07 02 07 03 04");

    /// <summary>Custom EQ list with zero saved profiles.</summary>
    public static readonly byte[] CustomEqEmpty = Hex("00 00");

    /// <summary>
    /// A 16-id misc request truncated to 15 entries in exactly 32 payload bytes.
    /// <c>0x1E</c> was requested and is missing; the count field says 15 as though that
    /// were the whole answer.
    /// </summary>
    public static readonly byte[] MiscTruncated = Hex(
        "00 0f 02 00 04 01 05 01 0b 00 0c 00 0d 01 11 01 " +
        "12 00 14 00 16 01 17 00 18 00 19 00 1b 00 1d 01");

    /// <summary>The ids that produced <see cref="MiscTruncated"/>.</summary>
    public static readonly byte[] MiscTruncatedRequest =
        [0x02, 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x11, 0x12, 0x14, 0x16, 0x17, 0x18, 0x19, 0x1B, 0x1D, 0x1E];

    /// <summary>Asking for only the two highest ids returns both, supported and enabled.</summary>
    public static readonly byte[] MiscTwoIds = Hex("00 02 1d 01 1e 01");

    /// <summary>Firmware. The payload is ASCII, not binary.</summary>
    public static readonly byte[] Firmware = BuildFirmware();

    private static byte[] BuildFirmware()
    {
        var text = System.Text.Encoding.ASCII.GetBytes("1,2,138,2,2,138,3,1,01,3,2,105");
        var payload = new byte[2 + text.Length];
        payload[0] = 0x00;
        payload[1] = 0x04;
        text.CopyTo(payload, 2);
        return payload;
    }

    /// <summary>
    /// SYNTHETIC. One connected peer, built to the documented layout
    /// <c>[MAC 6B reversed][attr][0x02][flag][nameLen][name]</c> using the real MAC and name
    /// this PC reported.
    /// </summary>
    public static readonly byte[] ConnectedDevices = BuildConnectedDevices();

    private static byte[] BuildConnectedDevices()
    {
        var name = System.Text.Encoding.UTF8.GetBytes("DESKPC");
        var payload = new List<byte> { 0x00, 0x01 };
        payload.AddRange(Hex("5f 4e 3d 2c 1b 0a"));
        payload.Add(0x09);
        payload.Add(0x02);
        payload.Add(0x01);
        payload.Add((byte)name.Length);
        payload.AddRange(name);
        return [.. payload];
    }

    /// <summary>
    /// SYNTHETIC. A three-record touch table in the documented
    /// <c>[side][type][gesture][action]</c> shape, using rows from the real read.
    /// </summary>
    public static readonly byte[] TouchConfig = Hex(
        "00 03 " +
        "01 01 02 05 " +
        "02 01 02 06 " +
        "01 06 02 1d");

    /// <summary>
    /// SYNTHETIC. One custom EQ profile named Custom1 with the six real band frequencies.
    /// Layout per docs: <c>[slot][min][max][unknown][nameLen][name][bandCount]</c> then
    /// <c>[freq16 LE][gain]</c> per band.
    /// </summary>
    public static readonly byte[] CustomEqOneProfile = BuildCustomEq();

    private static byte[] BuildCustomEq()
    {
        var name = System.Text.Encoding.ASCII.GetBytes("Custom1");
        var payload = new List<byte> { 0x00, 0x01, 0x01, 0xFA, 0x06, 0x04, (byte)name.Length };
        payload.AddRange(name);
        payload.Add(0x06);

        int[] frequencies = [62, 250, 1000, 4000, 8000, 16000];
        sbyte[] gains = [6, 6, 0, 0, -6, -6];
        for (var i = 0; i < frequencies.Length; i++)
        {
            payload.Add((byte)(frequencies[i] & 0xFF));
            payload.Add((byte)(frequencies[i] >> 8));
            payload.Add((byte)gains[i]);
        }

        return [.. payload];
    }
}
