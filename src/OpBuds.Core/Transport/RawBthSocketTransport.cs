using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace OpBuds.Core.Transport;

/// <summary>
/// Bluetooth Classic RFCOMM over a raw <c>AF_BTH</c> socket. The primary transport.
/// </summary>
/// <remarks>
/// <para>
/// This began as the plan's fallback and became the primary for a concrete reason: 32feet
/// 4.2.4 ships no <c>net8.0-windows</c> target, so on .NET 8 it silently resolves to its
/// <c>netstandard2.0</c> stub and throws <c>PlatformNotSupportedException</c> from
/// <c>BluetoothClient</c>'s constructor at runtime. Compiles clean, fails on the device.
/// </para>
/// <para>
/// The raw path has no such ambiguity, needs no package, and is the same approach the Python
/// probe has used successfully across every session on this machine. Windows still performs
/// the SDP resolve, because the endpoint carries a service GUID and port zero.
/// </para>
/// </remarks>
public sealed class RawBthSocketTransport : IBudsTransport
{
    private readonly BthEndPoint _endpoint;
    private readonly int _readBufferSize;
    private Socket? _socket;
    private NetworkStream? _stream;

    public RawBthSocketTransport(string macAddress, Guid serviceUuid, int readBufferSize = 4096)
    {
        _endpoint = new BthEndPoint(BthEndPoint.ParseAddress(macAddress), serviceUuid);
        _readBufferSize = readBufferSize;
        Description = $"{macAddress} rfcomm {serviceUuid}";
    }

    public bool IsConnected => _socket?.Connected == true;

    public string Description { get; }

    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var socket = new Socket(
            (AddressFamily)BthEndPoint.BluetoothAddressFamily,
            SocketType.Stream,
            (ProtocolType)BthEndPoint.RfcommProtocol);

        try
        {
            // Deliberately the *synchronous* connect, run off the caller's thread.
            // Socket.ConnectAsync goes through ConnectEx, which wants a bound socket and
            // does not support AF_BTH -- it fails with WSAEINVAL (10022) before the radio is
            // ever involved. The blocking path uses plain WSAConnect, which is exactly what
            // the Python probe calls through ctypes and what works on this stack.
            await Task.Run(() => socket.Connect(_endpoint), cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw Translate(ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
    }

    private static Exception Translate(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.AddressAlreadyInUse or SocketError.IsConnected or SocketError.ConnectionRefused =>
            new ChannelBusyException(
                "the earbuds' control channel refused the connection. It is most likely held " +
                "by another app - close the earbud settings page on your phone and try again.",
                ex),
        SocketError.HostDown or SocketError.HostUnreachable or SocketError.TimedOut =>
            new IOException(
                "the earbuds did not answer. Are they out of the case and connected?", ex),
        _ => ex,
    };

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        var buffer = new byte[_readBufferSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            Exception? error = null;

            try
            {
                read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // Buds returning to the case land here. An ordinary event, not a fault.
                read = 0;
                error = ex;
            }

            if (read == 0)
            {
                Disconnected?.Invoke(this, new TransportDisconnectedEventArgs(DisconnectReason.LinkLost, error));
                yield break;
            }

            yield return buffer.AsMemory(0, read).ToArray();
        }
    }

    public ValueTask DisposeAsync()
    {
        var wasConnected = IsConnected;

        _stream?.Dispose();
        _socket?.Dispose();
        _stream = null;
        _socket = null;

        if (wasConnected)
        {
            Disconnected?.Invoke(this, new TransportDisconnectedEventArgs(DisconnectReason.Requested));
        }

        return ValueTask.CompletedTask;
    }
}
