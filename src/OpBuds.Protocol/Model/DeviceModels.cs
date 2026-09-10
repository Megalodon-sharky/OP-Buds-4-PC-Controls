namespace OpBuds.Protocol.Model;

/// <summary>Which earpiece a record refers to.</summary>
public enum BudSide : byte
{
    Left = 0x01,
    Right = 0x02,
    Case = 0x03,
}

/// <summary>
/// ANC state. Values are the readback masks, which the set payload mirrors verbatim.
/// </summary>
/// <remarks>
/// All seven confirmed by writing the 16-bit little-endian set form and reading back.
/// The phone's single "Noise Cancellation" button selects <see cref="NoiseCancellingHigh"/>.
/// </remarks>
public enum AncMode : ushort
{
    Off = 0x0008,
    NoiseCancellingHigh = 0x0010,
    NoiseCancellingModerate = 0x0020,
    NoiseCancellingLow = 0x0040,
    NoiseCancellingAuto = 0x0080,
    Transparency = 0x0100,
    Adaptive = 0x0800,
}

/// <summary>
/// EQ preset. <c>0x03</c> and <c>0x05</c>-<c>0x07</c> are accepted by the firmware but the
/// phone never selects them and no audible effect has been confirmed, so they are not
/// modelled. Validate against this enum before transmitting: the device will not.
/// </summary>
public enum EqPreset : byte
{
    Balanced = 0x00,
    Serenade = 0x01,
    Bass = 0x02,
    Custom = 0x04,
}

/// <summary>One battery reading. <c>Percent</c> is the low seven bits of the raw byte.</summary>
public readonly record struct BatteryLevel(BudSide Side, int Percent, bool Charging)
{
    /// <summary>CONFIDENCE: assumed-gb. No charging sample exists in any capture.</summary>
    public const byte ChargingBit = 0x80;
}

/// <summary>
/// Wear state flags, proven from live state changes rather than inherited.
/// </summary>
public readonly record struct WearState(BudSide Side, byte Flags)
{
    public bool Present => (Flags & 0x04) != 0;

    public bool OutOfCase => (Flags & 0x01) != 0;

    public bool InEar => (Flags & 0x02) != 0;

    /// <summary>True when the bud is present but sitting in the case.</summary>
    public bool InCase => Present && !OutOfCase;
}

/// <summary>BassWave level and its advertised bounds. Levels are signed.</summary>
public readonly record struct BassWaveState(int Minimum, int Maximum, int Current);

/// <summary>One band of a custom EQ profile.</summary>
public readonly record struct EqBand(int FrequencyHz, int GainDb);

/// <summary>
/// A saved custom EQ profile. Band frequencies were matched exactly against the device:
/// 62, 250, 1000, 4000, 8000, 16000 Hz.
/// </summary>
public sealed record CustomEqProfile(
    byte Slot,
    string Name,
    int MinimumGainDb,
    int MaximumGainDb,
    IReadOnlyList<EqBand> Bands);

/// <summary>One touch-gesture binding: <c>[side][type][gesture][action]</c>.</summary>
/// <remarks>
/// Type <c>0x01</c> carries six gestures, type <c>0x06</c> only three, consistent with
/// <c>0x06</c> being the in-call context. <c>likely</c>
/// </remarks>
public readonly record struct TouchBinding(BudSide Side, byte ContextType, byte Gesture, byte Action);

/// <summary>One entry of the misc feature-switch list.</summary>
public readonly record struct MiscFeature(byte Id, byte Value)
{
    public bool Enabled => Value != 0;
}

/// <summary>A peer currently connected to the earbuds.</summary>
public sealed record ConnectedDevice(string MacAddress, string Name, byte Attribute, byte Flag);

/// <summary>Which earpieces to ring.</summary>
[Flags]
public enum FindTarget
{
    None = 0,
    Left = 1,
    Right = 2,
    Both = Left | Right,
}
