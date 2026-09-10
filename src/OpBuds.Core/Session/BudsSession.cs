using System.Collections.Concurrent;
using OpBuds.Core.Diagnostics;
using OpBuds.Core.Transport;
using OpBuds.Protocol.Framing;
using OpBuds.Protocol.Profiles;

namespace OpBuds.Core.Session;

public enum SessionState
{
    Disconnected,
    Connecting,

    /// <summary>Connected, but the device has not proved it is listening.</summary>
    Degraded,

    Connected,
}

/// <summary>
/// Frames, correlation and notification routing over one transport.
/// </summary>
/// <remarks>
/// <para>
/// <b>Requests are correlated on <c>(messageType, commandId)</c>, not on the sequence
/// byte.</b> The reference implementations disagree about how sequence is allocated, and
/// this hardware has never been observed echoing a non-zero request sequence back on a
/// reply — every capture shows replies carrying <c>0x00</c>. Keying on a value that may not
/// round-trip would strand every request. So at most one request per command id is in
/// flight, which costs nothing at this protocol's traffic levels and cannot mis-route.
/// </para>
/// <para>
/// Keying on the 16-bit <c>(type &lt;&lt; 8) | id</c> label would also collide:
/// <c>0x0106</c> and <c>0x8106</c> share an id, and notifications <c>0x0204</c>/<c>0x0205</c>
/// share one with ANC-set <c>0x0404</c>.
/// </para>
/// </remarks>
public sealed class BudsSession : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait for any frame before declaring the session degraded.</summary>
    private static readonly TimeSpan LivenessWindow = TimeSpan.FromSeconds(3);

    private readonly IBudsTransport _transport;
    private readonly IDeviceProfile _profile;
    private readonly ProtocolLog _log;
    private readonly FrameReader _reader = new();
    private readonly ISequenceAllocator _sequence;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private readonly ConcurrentDictionary<(byte Type, byte Id), TaskCompletionSource<OpFrame>> _pending = new();

    private Task? _receiveLoop;
    private int _framesReceived;

    public BudsSession(IBudsTransport transport, IDeviceProfile profile, ProtocolLog? log = null)
    {
        _transport = transport;
        _profile = profile;
        _log = log ?? ProtocolLog.Null;
        _sequence = profile.CreateSequenceAllocator();
    }

    public SessionState State { get; private set; } = SessionState.Disconnected;

    public IDeviceProfile Profile => _profile;

    /// <summary>Unsolicited frames: both notification families, plus JSON telemetry.</summary>
    public event EventHandler<OpFrame>? NotificationReceived;

    public event EventHandler<SessionState>? StateChanged;

    /// <summary>
    /// Connects, starts reading, then performs the handshake.
    /// </summary>
    /// <remarks>
    /// The reader starts <b>before</b> HELLO goes out, deliberately: the initial state
    /// broadcast lands immediately after the handshake and is the densest frame in the
    /// session. Starting the reader afterwards drops it.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        SetState(SessionState.Connecting);
        await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_lifetime.Token), CancellationToken.None);

        var handshake = _profile.Handshake;
        await SendRawAsync(handshake.Hello, "HELLO", cancellationToken).ConfigureAwait(false);

        if (handshake.RegisterRequired && !handshake.RegisterPayload.IsEmpty)
        {
            await Task.Delay(handshake.RegisterDelay, cancellationToken).ConfigureAwait(false);
            await SendRawAsync(handshake.RegisterPayload, "REGISTER", cancellationToken).ConfigureAwait(false);
        }

        SetState(SessionState.Degraded);
        await ProbeLivenessAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Promotes the session to <see cref="SessionState.Connected"/> once the device answers.
    /// </summary>
    /// <remarks>
    /// The handshake is fire-and-forget in every reference implementation, which means a
    /// silent failure looks exactly like a healthy connection. A liveness read turns that
    /// into an observable state instead of a mystery two layers up.
    /// </remarks>
    private async Task ProbeLivenessAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LivenessWindow;
        while (DateTime.UtcNow < deadline)
        {
            if (Volatile.Read(ref _framesReceived) > 0)
            {
                SetState(SessionState.Connected);
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        var reply = await RequestAsync(
            OpBuds.Protocol.Commands.OpCommands.BatteryGet,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (reply is not null)
        {
            SetState(SessionState.Connected);
        }
        else
        {
            _log.Note("no frame within the liveness window; session is Degraded");
        }
    }

    /// <summary>Sends a request and waits for its reply. Null on timeout.</summary>
    public Task<OpFrame?> RequestAsync(
        byte commandId,
        ReadOnlyMemory<byte> payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        ExchangeAsync(commandId, MessageTypes.Request, MessageTypes.Reply, payload, timeout, cancellationToken);

    /// <summary>
    /// Sends a set and waits for its ack. Null on timeout.
    /// </summary>
    /// <remarks>
    /// <b>An OK ack does not mean the command took effect.</b> Three find-device calls acked
    /// <c>0x00</c> and made no sound. Confirm a write by reading the getter back or by
    /// waiting for the matching state-change notification.
    /// </remarks>
    public Task<OpFrame?> SetAsync(
        byte commandId,
        ReadOnlyMemory<byte> payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!_profile.SetAllowlist.Contains(commandId))
        {
            // The 0x04xx space is never fuzzed: an unknown setter could be factory reset or
            // DFU entry. This is the last line of defence before bytes reach a radio.
            throw new InvalidOperationException(
                $"set command 0x04{commandId:X2} is not on this profile's allowlist");
        }

        return ExchangeAsync(commandId, MessageTypes.Set, MessageTypes.SetAck, payload, timeout, cancellationToken);
    }

    private async Task<OpFrame?> ExchangeAsync(
        byte commandId,
        byte sendType,
        byte expectType,
        ReadOnlyMemory<byte> payload,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var key = (expectType, commandId);
        var completion = new TaskCompletionSource<OpFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(key, completion))
        {
            throw new InvalidOperationException(
                $"a request for command id 0x{commandId:X2} is already in flight");
        }

        try
        {
            var frame = new OpFrame(commandId, sendType, _sequence.Next(commandId), payload);
            await SendRawAsync(FrameCodec.Encode(frame), OpBuds.Protocol.Commands.OpCommands.Name(frame.Command), cancellationToken)
                .ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout ?? DefaultTimeout);

            try
            {
                return await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Note($"timeout waiting for 0x{expectType:X2}{commandId:X2}");
                return null;
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private async Task SendRawAsync(ReadOnlyMemory<byte> data, string note, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _log.Io("tx", data.Span, note);
            await _transport.SendAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                _log.Io("rx", chunk.Span);
                _reader.Append(chunk.Span);

                while (_reader.TryReadFrame(out var frame))
                {
                    Interlocked.Increment(ref _framesReceived);
                    _log.Frame(frame.Command, frame.Sequence, frame.Payload.Span);
                    Dispatch(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            _log.Note($"receive loop ended: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            FailPending(new IOException("the session ended before a reply arrived"));
            SetState(SessionState.Disconnected);
        }
    }

    private void Dispatch(OpFrame frame)
    {
        if (frame.IsNotification)
        {
            NotificationReceived?.Invoke(this, frame);
            return;
        }

        if (_pending.TryGetValue((frame.MessageType, frame.CommandId), out var completion))
        {
            completion.TrySetResult(frame);
            return;
        }

        // Late replies after a timeout land here. Worth a line in the log, not an error:
        // it usually means the timeout is too tight rather than that anything is broken.
        _log.Note($"unmatched frame 0x{frame.Command:X4}");
    }

    private void FailPending(Exception error)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
            {
                completion.TrySetException(error);
            }
        }
    }

    private void SetState(SessionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}
