using System.Net;
using System.Net.Sockets;

namespace OpBuds.Core.Transport;

/// <summary>
/// A Windows <c>SOCKADDR_BTH</c>, serialised by hand.
/// </summary>
/// <remarks>
/// <para>
/// .NET has no Bluetooth endpoint type, so the 30-byte structure is written directly:
/// </para>
/// <code>
/// offset  0  USHORT     addressFamily   = AF_BTH (32)
/// offset  2  ULONGLONG  btAddr          little-endian, six bytes used
/// offset 10  GUID       serviceClassId  native layout, as Guid.ToByteArray gives it
/// offset 26  ULONG      port            little-endian; 0 means "resolve via SDP"
/// </code>
/// <para>
/// Leaving <see cref="Port"/> at zero while setting <see cref="ServiceClassId"/> is what
/// makes Windows do the SDP lookup. That is the whole reason this project never needs to
/// know the RFCOMM channel number — the published implementations all hardcode channel 15,
/// and that number has never been required here.
/// </para>
/// </remarks>
public sealed class BthEndPoint(ulong address, Guid serviceClassId, uint port = 0) : EndPoint
{
    /// <summary>AF_BTH.</summary>
    public const int BluetoothAddressFamily = 32;

    /// <summary>BTHPROTO_RFCOMM.</summary>
    public const int RfcommProtocol = 3;

    private const int StructureSize = 30;

    public ulong Address { get; } = address;

    public Guid ServiceClassId { get; } = serviceClassId;

    public uint Port { get; } = port;

    public override AddressFamily AddressFamily => (AddressFamily)BluetoothAddressFamily;

    /// <summary>Parses <c>12:34:56:78:9A:BC</c> or <c>123456789ABC</c>.</summary>
    public static ulong ParseAddress(string mac)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);

        var digits = mac.Replace(":", string.Empty).Replace("-", string.Empty).Trim();
        if (digits.Length != 12)
        {
            throw new FormatException($"'{mac}' is not a 48-bit Bluetooth address");
        }

        return Convert.ToUInt64(digits, 16);
    }

    public static string FormatAddress(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> ((5 - i) * 8)) & 0xFF).ToString("X2")));

    public override SocketAddress Serialize()
    {
        var socketAddress = new SocketAddress(AddressFamily, StructureSize);

        // SocketAddress writes the family into bytes 0-1 itself; everything after is ours.
        for (var i = 0; i < 8; i++)
        {
            socketAddress[2 + i] = (byte)((Address >> (8 * i)) & 0xFF);
        }

        var guid = ServiceClassId.ToByteArray();
        for (var i = 0; i < guid.Length; i++)
        {
            socketAddress[10 + i] = guid[i];
        }

        for (var i = 0; i < 4; i++)
        {
            socketAddress[26 + i] = (byte)((Port >> (8 * i)) & 0xFF);
        }

        return socketAddress;
    }

    public override EndPoint Create(SocketAddress socketAddress)
    {
        ArgumentNullException.ThrowIfNull(socketAddress);

        ulong address = 0;
        for (var i = 0; i < 8; i++)
        {
            address |= (ulong)socketAddress[2 + i] << (8 * i);
        }

        var guid = new byte[16];
        for (var i = 0; i < guid.Length; i++)
        {
            guid[i] = socketAddress[10 + i];
        }

        uint port = 0;
        for (var i = 0; i < 4; i++)
        {
            port |= (uint)socketAddress[26 + i] << (8 * i);
        }

        return new BthEndPoint(address, new Guid(guid), port);
    }

    public override string ToString() =>
        $"{FormatAddress(Address)} {ServiceClassId}" + (Port == 0 ? " (SDP)" : $" ch{Port}");
}
