using OpBuds.Protocol.Framing;
using OpBuds.Protocol.Model;

namespace OpBuds.Protocol.Profiles;

/// <summary>
/// The opening exchange.
/// </summary>
/// <remarks>
/// <see cref="Hello"/> is stored as the full ten bytes seen on the wire, trailing
/// <c>0x12</c> included. That byte sits <i>outside</i> the frame its own length field
/// declares — it is unexplained, byte-identical across three independent implementations,
/// and it works, so it is replayed verbatim rather than tidied away.
/// </remarks>
public sealed record HandshakeSpec(
    ReadOnlyMemory<byte> Hello,
    TimeSpan RegisterDelay,
    ReadOnlyMemory<byte> RegisterPayload,
    bool RegisterRequired);

/// <summary>A capability the UI can surface.</summary>
public enum BudsCapability
{
    Battery,
    WearDetection,
    Anc,

    /// <summary>ANC sub-levels: high, moderate, low, auto.</summary>
    AncLevels,

    FindDevice,
    EqPresets,
    CustomEq,
    BassWave,
    TouchRemap,
    ConnectedDevices,
    Firmware,

    /// <summary>Hi-res / LHDC. Read-only on Windows: the MS Bluetooth stack offers SBC and AAC only.</summary>
    HiResStatus,
}

/// <summary>
/// What a connected device can do.
/// </summary>
/// <remarks>
/// <para>
/// An earlier design required this to be re-read on every state change, because features
/// appeared to leave the supported list at runtime. That was <b>refuted</b>: the misc-config
/// handler truncates its reply at 32 bytes and misreports the count, so an over-long request
/// loses its highest ids silently. The list is static.
/// </para>
/// <para>
/// It is therefore safe to build this once per connection — provided the read that feeds it
/// pages its requests. See <see cref="Codecs.MiscConfigCodec.Pages"/>.
/// </para>
/// </remarks>
public sealed class FeatureSet(IEnumerable<BudsCapability> capabilities)
{
    private readonly HashSet<BudsCapability> _capabilities = [.. capabilities];

    public bool Has(BudsCapability capability) => _capabilities.Contains(capability);

    public IReadOnlyCollection<BudsCapability> All => _capabilities;

    /// <summary>
    /// Capabilities deliberately excluded, with the reason, so the UI hides rather than
    /// disables them and nobody re-investigates a settled question.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>
    {
        ["SpatialAudio"] =
            "refuted: OnePlus 3D Audio is a phone-side upmix. Toggling it produced zero change " +
            "across all fifty responding commands with the push stream open. There is no " +
            "bud-side setting to expose.",
        ["GoldenSound"] =
            "deferred by the project owner. A multi-step guided calibration, not a single opcode.",
        ["FirmwareUpdate"] =
            "permanently out of scope: a botched update bricks the hardware with no recovery path.",
    };
}

/// <summary>
/// Everything that varies between earbud models.
/// </summary>
/// <remarks>
/// This is the load-bearing abstraction of the library, because almost every Buds 4 constant
/// began life as a Buds Pro 3 guess and several turned out to be wrong. Keeping them behind
/// a profile means a corrected constant is a one-line change in one place.
/// </remarks>
public interface IDeviceProfile
{
    string Name { get; }

    /// <summary>
    /// The OPO control service. Connect by UUID and let the platform resolve the channel —
    /// the channel number is never reported and never needed.
    /// </summary>
    Guid ServiceUuid { get; }

    HandshakeSpec Handshake { get; }

    /// <summary>Set command ids this profile permits. The <c>0x04xx</c> space is never fuzzed.</summary>
    IReadOnlySet<byte> SetAllowlist { get; }

    IReadOnlyList<byte> MiscFeatureIds { get; }

    /// <summary>Ids per misc request. Exceeding it truncates the reply without an error.</summary>
    int MiscMaxIdsPerRequest { get; }

    IReadOnlySet<AncMode> SupportedAncModes { get; }

    IReadOnlySet<EqPreset> SupportedEqPresets { get; }

    FeatureSet DefaultFeatures { get; }

    ISequenceAllocator CreateSequenceAllocator();
}
