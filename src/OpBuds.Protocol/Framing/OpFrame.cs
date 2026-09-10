namespace OpBuds.Protocol.Framing;

/// <summary>
/// One decoded protocol frame.
/// </summary>
/// <remarks>
/// <para>
/// Byte 4 of the wire format is the command id and byte 5 is the message type. The reference
/// implementations describe those two bytes as a single little-endian 16-bit command, which
/// breaks on REGISTER (<c>..00 85..</c> would read as "command 0x8500"). The familiar
/// <c>0x0106</c> / <c>0x8106</c> labels survive as names via <see cref="Command"/>, but
/// request/reply correlation must key on <see cref="CommandId"/> and
/// <see cref="MessageType"/> separately: <c>0x0106</c> and <c>0x8106</c> share an id, and
/// notifications <c>0x0204</c> / <c>0x0205</c> share an id with ANC-set <c>0x0404</c>.
/// </para>
/// </remarks>
public readonly record struct OpFrame(
    byte CommandId,
    byte MessageType,
    byte Sequence,
    ReadOnlyMemory<byte> Payload)
{
    /// <summary>The <c>(type &lt;&lt; 8) | id</c> label used throughout the documentation.</summary>
    public ushort Command => (ushort)((MessageType << 8) | CommandId);

    /// <summary>True for both unsolicited notification families.</summary>
    public bool IsNotification => MessageType is MessageTypes.Notify or MessageTypes.NotifyAlt;

    /// <summary>
    /// The status byte, where the payload carries one.
    /// </summary>
    /// <remarks>
    /// Not universal: several replies begin with a value that is not a status. Use
    /// <see cref="Ok"/> only for message types known to carry one — chiefly acks.
    /// </remarks>
    public byte Status => Payload.Length == 0 ? (byte)0 : Payload.Span[0];

    /// <summary>
    /// Whether the frame's status byte is zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An OK ack does not mean the command took effect.</b> Three find-device calls
    /// returned status <c>0x00</c> and produced no sound at all. The ack confirms the frame
    /// parsed and the arguments were in range, nothing more.
    /// </para>
    /// <para>
    /// Confirm a write by reading the corresponding getter back, or by waiting for the
    /// matching <c>0x05xx</c> state-change notification. Never treat a bare ack as applied.
    /// </para>
    /// </remarks>
    public bool Ok => Status == 0;

    public override string ToString() =>
        $"0x{Command:X4} seq=0x{Sequence:X2} payload={Payload.Length}B";
}

/// <summary>Byte 5 of the frame.</summary>
public static class MessageTypes
{
    public const byte Request = 0x01;
    public const byte Notify = 0x02;
    public const byte Set = 0x04;
    public const byte NotifyAlt = 0x05;
    public const byte Reply = 0x81;
    public const byte SetAck = 0x84;
    public const byte Register = 0x85;
}
