namespace OpBuds.Core.Transport;

/// <summary>Why a transport stopped.</summary>
public enum DisconnectReason
{
    /// <summary>Closed by us.</summary>
    Requested,

    /// <summary>The peer or the stack dropped the link.</summary>
    LinkLost,

    /// <summary>The vendor channel refused the connection, typically because it is in use.</summary>
    ChannelBusy,

    Error,
}

public sealed class TransportDisconnectedEventArgs(DisconnectReason reason, Exception? error = null) : EventArgs
{
    public DisconnectReason Reason { get; } = reason;

    public Exception? Error { get; } = error;
}

/// <summary>
/// The only seam that touches hardware.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReceiveAsync"/> yields <b>wire chunks exactly as the stack delivered them</b>,
/// never frames. Chunk boundaries carry no protocol meaning and a single reply routinely
/// arrives split across several reads, so reassembly belongs to
/// <see cref="Protocol.Framing.FrameReader"/> — one layer up, where it is testable without a
/// radio.
/// </para>
/// <para>
/// Keeping that split honest is what lets a recorded capture drive the entire stack: real
/// chunk boundaries replayed from JSONL become free fuzz coverage for the frame reader.
/// </para>
/// </remarks>
public interface IBudsTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Human description of the peer, for logs and error messages.</summary>
    string Description { get; }

    event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Wire chunks, as received. Completes when the link closes.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when the vendor control channel is already in use.
/// </summary>
/// <remarks>
/// Worth its own type because the remedy is specific and user-facing — close the earbud
/// settings page on the phone — and because retry-storming a busy channel helps nobody.
/// </remarks>
public sealed class ChannelBusyException(string message, Exception? inner = null)
    : IOException(message, inner);
