using OpBuds.Protocol.Codecs;
using OpBuds.Protocol.Commands;
using OpBuds.Protocol.Framing;
using OpBuds.Protocol.Model;

namespace OpBuds.Protocol.Profiles;

/// <summary>
/// OnePlus Buds 4.
/// </summary>
/// <remarks>
/// Every constant here was measured on hardware unless its member says otherwise. Where a
/// value was inherited from the Buds Pro 3 and later refuted, the refutation is recorded
/// rather than the value quietly replaced — see <see cref="BudsPro3Profile"/>, which is kept
/// as a regression target precisely so a refactor that breaks Buds 4 silently breaks Pro 3
/// visibly.
/// </remarks>
public sealed class Buds4Profile : IDeviceProfile
{
    /// <summary>
    /// The classic HELLO, ten bytes including the trailing <c>0x12</c> that sits outside the
    /// frame. Works verbatim on this hardware; every read command answers afterwards.
    /// </summary>
    /// <remarks>
    /// Must be declared <b>before</b> <see cref="Instance"/>: static field initialisers run in
    /// declaration order, so an <c>Instance</c> built above this line would capture a null.
    /// </remarks>
    private static readonly byte[] HelloBytes =
        [0xAA, 0x07, 0x00, 0x00, 0x00, 0x01, 0x23, 0x00, 0x00, 0x12];

    public static readonly Buds4Profile Instance = new();

    public string Name => "OnePlus Buds 4";

    public Guid ServiceUuid { get; } = new("0000079a-d102-11e1-9b23-00025b00a5a5");

    /// <summary>
    /// REGISTER draws no reply and appears unnecessary on this model, so it is sent but not
    /// required. The reader must already be running before HELLO goes out: the initial state
    /// broadcast lands immediately afterwards and is the densest frame in the session.
    /// </summary>
    public HandshakeSpec Handshake { get; } = new(
        Hello: HelloBytes,
        RegisterDelay: TimeSpan.FromSeconds(1),
        RegisterPayload: ReadOnlyMemory<byte>.Empty,
        RegisterRequired: false);

    public IReadOnlySet<byte> SetAllowlist => OpCommands.SetAllowlist;

    public IReadOnlyList<byte> MiscFeatureIds => MiscConfigCodec.KnownIds;

    public int MiscMaxIdsPerRequest => MiscConfigCodec.MaxIdsPerReply;

    /// <summary>All seven confirmed by writing the 16-bit form and reading the mode back.</summary>
    public IReadOnlySet<AncMode> SupportedAncModes { get; } = new HashSet<AncMode>
    {
        AncMode.Off,
        AncMode.NoiseCancellingHigh,
        AncMode.NoiseCancellingModerate,
        AncMode.NoiseCancellingLow,
        AncMode.NoiseCancellingAuto,
        AncMode.Transparency,
        AncMode.Adaptive,
    };

    /// <summary>
    /// The four the phone selects. The firmware also accepts <c>0x03</c> and
    /// <c>0x05</c>-<c>0x07</c> without complaint, which is exactly why this set exists.
    /// </summary>
    public IReadOnlySet<EqPreset> SupportedEqPresets { get; } = new HashSet<EqPreset>
    {
        EqPreset.Balanced, EqPreset.Serenade, EqPreset.Bass, EqPreset.Custom,
    };

    public FeatureSet DefaultFeatures { get; } = new(
    [
        BudsCapability.Battery,
        BudsCapability.WearDetection,
        BudsCapability.Anc,
        BudsCapability.AncLevels,
        BudsCapability.FindDevice,
        BudsCapability.EqPresets,
        BudsCapability.CustomEq,
        BudsCapability.BassWave,
        BudsCapability.TouchRemap,
        BudsCapability.ConnectedDevices,
        BudsCapability.Firmware,
        BudsCapability.HiResStatus,
    ]);

    /// <summary>
    /// Rolling, matching Gadgetbridge. The device echoes the sequence back on replies and has
    /// never rejected one, so correlation works either way; rolling is chosen because it makes
    /// concurrent in-flight requests distinguishable.
    /// </summary>
    public ISequenceAllocator CreateSequenceAllocator() => new RollingSequenceAllocator();
}
