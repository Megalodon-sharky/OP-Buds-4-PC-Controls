using OpBuds.Protocol.Commands;
using OpBuds.Protocol.Framing;
using OpBuds.Protocol.Model;

namespace OpBuds.Protocol.Profiles;

/// <summary>
/// OnePlus Buds Pro 3, as described by the reference implementations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not verified against hardware.</b> Nobody on this project owns a Pro 3. This profile
/// exists as a <i>regression target</i>: the Buds 4 constants started as copies of these, and
/// several turned out to be wrong. Keeping the original values in the tree means a refactor
/// that breaks the Buds 4 silently will break this profile visibly.
/// </para>
/// <para>
/// Differences already established: the Pro 3 five-preset EQ map does not apply (the Buds 4
/// has three plus a custom slot), and Gadgetbridge's misc ids <c>ldac</c> (<c>0x01</c>) and
/// <c>multipoint</c> (<c>0x03</c>) are absent on the Buds 4 entirely.
/// </para>
/// </remarks>
public sealed class BudsPro3Profile : IDeviceProfile
{
    // Declared before Instance on purpose: static field initialisers run in declaration order.
    private static readonly byte[] HelloBytes =
        [0xAA, 0x07, 0x00, 0x00, 0x00, 0x01, 0x23, 0x00, 0x00, 0x12];

    public static readonly BudsPro3Profile Instance = new();

    public string Name => "OnePlus Buds Pro 3 (reference, unverified)";

    public Guid ServiceUuid { get; } = new("0000079a-d102-11e1-9b23-00025b00a5a5");

    public HandshakeSpec Handshake { get; } = new(
        Hello: HelloBytes,
        RegisterDelay: TimeSpan.FromSeconds(1),
        RegisterPayload: ReadOnlyMemory<byte>.Empty,
        RegisterRequired: true);

    public IReadOnlySet<byte> SetAllowlist => OpCommands.SetAllowlist;

    /// <summary>Gadgetbridge's ids, including the two the Buds 4 does not have.</summary>
    public IReadOnlyList<byte> MiscFeatureIds { get; } = [0x01, 0x02, 0x03, 0x04];

    /// <summary>Unknown for this model; the 32-byte cap was measured on a Buds 4.</summary>
    public int MiscMaxIdsPerRequest => 15;

    public IReadOnlySet<AncMode> SupportedAncModes { get; } = new HashSet<AncMode>
    {
        AncMode.Off, AncMode.NoiseCancellingHigh, AncMode.Transparency,
    };

    /// <summary>
    /// The Pro 3 map is <c>balanced/bold/serenade/bass/dynaudio</c> at
    /// <c>0x00/0x01/0x02/0x03/0x07</c>. Only the values overlapping this library's
    /// <see cref="EqPreset"/> enum are expressible here.
    /// </summary>
    public IReadOnlySet<EqPreset> SupportedEqPresets { get; } = new HashSet<EqPreset>
    {
        EqPreset.Balanced, EqPreset.Serenade, EqPreset.Bass,
    };

    public FeatureSet DefaultFeatures { get; } = new(
    [
        BudsCapability.Battery,
        BudsCapability.WearDetection,
        BudsCapability.Anc,
        BudsCapability.FindDevice,
        BudsCapability.EqPresets,
    ]);

    /// <summary>Fixed per command, as <c>nic0manz</c> does.</summary>
    public ISequenceAllocator CreateSequenceAllocator() => new FixedSequenceAllocator(
        new Dictionary<byte, byte>
        {
            [OpCommands.BatteryGet] = 0x25,
            [OpCommands.AncSet] = 0x42,
        });
}
