using System.Threading.Channels;
using OpBuds.Core.Transport;
using OpBuds.Protocol.Framing;

namespace OpBuds.Core.Tests;

/// <summary>
/// A transport that answers what it is asked, so the session can be driven without a radio.
/// </summary>
/// <remarks>
/// <see cref="ReplayTransport"/> cannot do this: it pushes its chunks the moment the loop
/// starts, so a reply always lands before the request that would have matched it. Anything
/// testing correlation needs a transport that reacts.
/// </remarks>
internal sealed class ScriptedTransport : IBudsTransport
{
    private readonly Dictionary<(byte Type, byte Id), byte[]> _responses = [];

    private readonly Channel<ReadOnlyMemory<byte>> _inbound =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    private readonly List<byte[]> _sent = [];

    public bool IsConnected { get; private set; }

    public string Description => "scripted";

    public IReadOnlyList<byte[]> Sent => _sent;

    /// <summary>Chunk size to split each reply into, to exercise reassembly.</summary>
    public int? SplitEvery { get; init; }

    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    /// <summary>Answers a request for <paramref name="commandId"/> with <paramref name="payload"/>.</summary>
    public ScriptedTransport Respond(byte sendType, byte commandId, byte replyType, byte[] payload)
    {
        _responses[(sendType, commandId)] = FrameCodec.Encode(
            new OpFrame(commandId, replyType, 0x00, payload));
        return this;
    }

    /// <summary>Pushes an unsolicited frame.</summary>
    public void Push(byte commandId, byte messageType, byte[] payload) =>
        _inbound.Writer.TryWrite(FrameCodec.Encode(new OpFrame(commandId, messageType, 0xFF, payload)));

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        _sent.Add(data.ToArray());

        if (FrameCodec.TryDecode(data.Span, out var frame, out _) == DecodeStatus.Ok
            && _responses.TryGetValue((frame.MessageType, frame.CommandId), out var reply))
        {
            foreach (var chunk in Split(reply))
            {
                _inbound.Writer.TryWrite(chunk);
            }
        }

        return Task.CompletedTask;
    }

    private IEnumerable<byte[]> Split(byte[] reply)
    {
        if (SplitEvery is not { } size || size <= 0)
        {
            yield return reply;
            yield break;
        }

        for (var offset = 0; offset < reply.Length; offset += size)
        {
            yield return reply[offset..Math.Min(offset + size, reply.Length)];
        }
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _inbound.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _inbound.Writer.TryComplete();
        Disconnected?.Invoke(this, new TransportDisconnectedEventArgs(DisconnectReason.Requested));
        return ValueTask.CompletedTask;
    }
}
