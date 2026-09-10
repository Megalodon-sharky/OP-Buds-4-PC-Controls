using OpBuds.Protocol.Framing;

namespace OpBuds.Protocol.Commands;

/// <summary>
/// Command ids, as measured. Names use the conventional <c>(type &lt;&lt; 8) | id</c> labels.
/// </summary>
/// <remarks>
/// Mirrors <c>tools/probe/opbuds_probe/known.py</c>. That file and this one are the two
/// halves of the same table; change them together.
/// </remarks>
public static class OpCommands
{
    // --- Requests (type 0x01) -> replies (0x81) ---------------------------
    public const byte FirmwareGet = 0x05;
    public const byte BatteryGet = 0x06;
    public const byte TouchConfigGet = 0x08;
    public const byte WearGet = 0x09;
    public const byte AncGet = 0x0C;
    public const byte MiscConfigGet = 0x0D;
    public const byte EqPresetGet = 0x0F;
    public const byte ConnectedDevicesGet = 0x12;

    /// <summary>The 299-byte table. Contents unresolved; see docs/protocol.md.</summary>
    public const byte UnknownTableGet = 0x1F;

    public const byte CustomEqGet = 0x22;
    public const byte BassWaveGet = 0x24;

    // --- Sets (type 0x04) -> acks (0x84) ----------------------------------

    /// <summary>Find device. Payload is a count-prefixed id list: <c>01 01</c> = left.</summary>
    public const byte FindDeviceSet = 0x00;

    /// <summary>CONFIDENCE: assumed-pro3. Never transmitted.</summary>
    public const byte TouchConfigSet = 0x01;

    /// <summary>CONFIDENCE: assumed-pro3. Never transmitted.</summary>
    public const byte MiscConfigSet = 0x03;

    /// <summary>CONFIDENCE: confirmed. See <see cref="Codecs.AncCodec"/>.</summary>
    public const byte AncSet = 0x04;

    /// <summary>CONFIDENCE: confirmed. Accepts any byte 0x00-0x07 without validation.</summary>
    public const byte EqPresetSet = 0x06;

    /// <summary>CONFIDENCE: assumed-pro3. Never transmitted.</summary>
    public const byte CustomEqSet = 0x18;

    /// <summary>CONFIDENCE: assumed-pro3. Never transmitted.</summary>
    public const byte BassWaveSet = 0x1B;

    // --- Notifications ----------------------------------------------------
    public const byte SubscriptionNotify = 0x04;

    /// <summary>
    /// Set ids that this library will ever transmit.
    /// </summary>
    /// <remarks>
    /// The <c>0x04xx</c> space is never swept or fuzzed: an unknown setter could be factory
    /// reset or DFU entry. Anything not listed here is refused before it reaches a socket.
    /// </remarks>
    public static readonly IReadOnlySet<byte> SetAllowlist = new HashSet<byte>
    {
        FindDeviceSet, TouchConfigSet, MiscConfigSet, AncSet, EqPresetSet, CustomEqSet, BassWaveSet,
    };

    private static readonly Dictionary<ushort, string> Names = new()
    {
        [0x0100] = "HELLO",
        [0x8500] = "REGISTER",
        [Label(MessageTypes.Request, FirmwareGet)] = "FIRMWARE_GET",
        [Label(MessageTypes.Request, BatteryGet)] = "BATTERY_GET",
        [Label(MessageTypes.Request, TouchConfigGet)] = "TOUCH_CONFIG_GET",
        [Label(MessageTypes.Request, WearGet)] = "WEAR_GET",
        [Label(MessageTypes.Request, AncGet)] = "ANC_GET",
        [Label(MessageTypes.Request, MiscConfigGet)] = "MISC_CONFIG_GET",
        [Label(MessageTypes.Request, EqPresetGet)] = "EQ_PRESET_GET",
        [Label(MessageTypes.Request, ConnectedDevicesGet)] = "CONNECTED_DEVICES_GET",
        [Label(MessageTypes.Request, CustomEqGet)] = "CUSTOM_EQ_GET",
        [Label(MessageTypes.Request, BassWaveGet)] = "BASSWAVE_GET",
        [Label(MessageTypes.Set, FindDeviceSet)] = "FIND_DEVICE_SET",
        [Label(MessageTypes.Set, TouchConfigSet)] = "TOUCH_CONFIG_SET",
        [Label(MessageTypes.Set, MiscConfigSet)] = "MISC_CONFIG_SET",
        [Label(MessageTypes.Set, AncSet)] = "ANC_SET",
        [Label(MessageTypes.Set, EqPresetSet)] = "EQ_PRESET_SET",
        [Label(MessageTypes.Set, CustomEqSet)] = "CUSTOM_EQ_SET",
        [Label(MessageTypes.Set, BassWaveSet)] = "BASSWAVE_SET",
        [Label(MessageTypes.Notify, SubscriptionNotify)] = "SUBSCRIPTION_NOTIFY",
    };

    /// <summary>Builds the conventional 16-bit label.</summary>
    public static ushort Label(byte messageType, byte commandId) =>
        (ushort)((messageType << 8) | commandId);

    /// <summary>Human name for a label, resolving reply and ack forms back to their request.</summary>
    public static string Name(ushort command)
    {
        if (Names.TryGetValue(command, out var name))
        {
            return name;
        }

        var id = (byte)(command & 0xFF);
        var type = (byte)(command >> 8);

        if (type == MessageTypes.Reply && Names.TryGetValue(Label(MessageTypes.Request, id), out name))
        {
            return name + " reply";
        }

        if (type == MessageTypes.SetAck && Names.TryGetValue(Label(MessageTypes.Set, id), out name))
        {
            return name + " ack";
        }

        return type is MessageTypes.Notify or MessageTypes.NotifyAlt
            ? $"notification (id 0x{id:X2})"
            : "unknown";
    }
}
