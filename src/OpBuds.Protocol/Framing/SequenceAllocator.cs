namespace OpBuds.Protocol.Framing;

/// <summary>
/// Supplies the sequence byte for an outgoing frame.
/// </summary>
/// <remarks>
/// The reference implementations disagree: <c>nic0manz</c> uses a fixed value per command,
/// Gadgetbridge rolls a counter. Both must be selectable per profile and both must be
/// testable, so this is an interface rather than a static.
/// </remarks>
public interface ISequenceAllocator
{
    byte Next(byte commandId);
}

/// <summary>Rolls 0x00-0xFF, skipping 0xFF because the device uses it for notifications.</summary>
public sealed class RollingSequenceAllocator : ISequenceAllocator
{
    private int _next;

    public RollingSequenceAllocator(byte start = 0x00) => _next = start;

    public byte Next(byte commandId)
    {
        var value = (byte)Interlocked.Increment(ref _next);
        return value == 0xFF ? (byte)Interlocked.Increment(ref _next) : value;
    }
}

/// <summary>
/// Returns the same sequence byte for a given command every time, as <c>nic0manz</c> does.
/// </summary>
public sealed class FixedSequenceAllocator(IReadOnlyDictionary<byte, byte>? overrides = null, byte fallback = 0x00)
    : ISequenceAllocator
{
    private readonly IReadOnlyDictionary<byte, byte> _overrides =
        overrides ?? new Dictionary<byte, byte>();

    public byte Next(byte commandId) =>
        _overrides.TryGetValue(commandId, out var value) ? value : fallback;
}
