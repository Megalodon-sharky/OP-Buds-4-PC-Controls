using OpBuds.Core.Diagnostics;
using OpBuds.Core.Transport;
using OpBuds.Protocol.Profiles;

namespace OpBuds.Core.Session;

/// <summary>
/// What the supervisor is doing, in a form the UI can bind to without interpreting it.
/// </summary>
/// <remarks>
/// <see cref="Summary"/> is deliberately part of the record rather than computed in a view
/// model: the difference between "Disconnected" and "In case" is a protocol-level judgement
/// about <i>why</i> the link dropped, and that knowledge lives here, not in XAML.
/// </remarks>
public sealed record SupervisorStatus(
    SessionState State,
    string Summary,
    int Attempt = 0,
    TimeSpan? RetryIn = null,
    DisconnectReason? Reason = null,
    bool InCase = false,
    bool ChannelBusy = false)
{
    /// <summary>True while the link is usable for requests.</summary>
    public bool IsUsable => State is SessionState.Connected or SessionState.Degraded;

    /// <summary>
    /// True when the drop is expected and a toast would be noise.
    /// </summary>
    /// <remarks>
    /// Buds returning to the case is the single most common disconnect and it is not a
    /// fault. Surfacing it as an error trains the user to ignore real errors.
    /// </remarks>
    public bool IsQuiet => InCase || State is SessionState.Connected or SessionState.Connecting;
}

/// <summary>
/// Keeps a session alive across case cycles, sleep, and a phone stealing the channel.
/// </summary>
/// <remarks>
/// <para>
/// Deferred out of Stage 3 on purpose. Its whole job is lifetime management, and lifetime
/// only becomes concrete once something owns it — a tray icon that outlives the window.
/// Written against a guess it would have been a different class.
/// </para>
/// <para>
/// The delay is injected so the backoff ladder can be tested in milliseconds. Every other
/// part of the loop is the real one.
/// </para>
/// </remarks>
public sealed class ConnectionSupervisor : IAsyncDisposable
{
    /// <summary>The ladder, capped. Reset to the first rung on any successful connect.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    private readonly Func<IBudsTransport> _transportFactory;
    private readonly IDeviceProfile _profile;
    private readonly ProtocolLog _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Random _random;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly object _wakeGate = new();
    private CancellationTokenSource? _wake;

    private Task? _loop;
    private BudsSession? _session;
    private int _attempt;

    public ConnectionSupervisor(
        Func<IBudsTransport> transportFactory,
        IDeviceProfile profile,
        ProtocolLog? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Random? random = null)
    {
        _transportFactory = transportFactory;
        _profile = profile;
        _log = log ?? ProtocolLog.Null;
        _delay = delay ?? Task.Delay;
        _random = random ?? new Random();
    }

    /// <summary>The live client, or null while disconnected. Re-created on every reconnect.</summary>
    public BudsClient? Client { get; private set; }

    public SupervisorStatus Status { get; private set; } =
        new(SessionState.Disconnected, "Not started");

    public event EventHandler<SupervisorStatus>? StatusChanged;

    /// <summary>Raised once per successful connect, after the client is usable.</summary>
    public event EventHandler<BudsClient>? Connected;

    /// <summary>Unsolicited frames from whichever session is current.</summary>
    public event EventHandler<Protocol.Framing.OpFrame>? NotificationReceived;

    /// <summary>
    /// Set from the last known wear reading so a drop can be described honestly.
    /// </summary>
    /// <remarks>
    /// The transport cannot distinguish "in the case" from "switched off" — both surface as
    /// a dead link. The only evidence available is what the buds last said about themselves,
    /// so the caller feeds it back in.
    /// </remarks>
    public bool LastKnownInCase { get; set; }

    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("the supervisor is already running");
        }

        _loop = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    }

    /// <summary>
    /// Cuts short the current backoff wait.
    /// </summary>
    /// <remarks>
    /// Called on resume-from-sleep and when the user asks for a reconnect. Waiting out a
    /// 30-second rung after the machine has just woken up is the difference between an app
    /// that feels broken and one that does not.
    /// </remarks>
    public void RequestReconnectNow()
    {
        lock (_wakeGate)
        {
            _wake?.Cancel();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await AttemptAsync(cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var wait = NextDelay(outcome);
            Publish(new SupervisorStatus(
                SessionState.Disconnected,
                DescribeWait(outcome, wait),
                _attempt,
                wait,
                outcome.Reason,
                outcome.InCase,
                outcome.ChannelBusy));

            await WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }

        Publish(new SupervisorStatus(SessionState.Disconnected, "Stopped"));
    }

    private readonly record struct AttemptResult(
        DisconnectReason? Reason, bool InCase, bool ChannelBusy, bool Succeeded);

    private async Task<AttemptResult> AttemptAsync(CancellationToken cancellationToken)
    {
        Publish(new SupervisorStatus(SessionState.Connecting, "Connecting", _attempt + 1));

        IBudsTransport? transport = null;
        BudsSession? session = null;
        var reason = DisconnectReason.LinkLost;

        void OnTransportDisconnected(object? sender, TransportDisconnectedEventArgs e) => reason = e.Reason;

        try
        {
            transport = _transportFactory();
            transport.Disconnected += OnTransportDisconnected;

            session = new BudsSession(transport, _profile, _log);
            session.NotificationReceived += OnNotification;

            await session.StartAsync(cancellationToken).ConfigureAwait(false);

            _attempt = 0;
            _session = session;
            Client = new BudsClient(session);

            Publish(new SupervisorStatus(
                session.State,
                session.State == SessionState.Connected ? "Connected" : "Connected, not answering"));

            Connected?.Invoke(this, Client);

            await WaitForEndAsync(session, cancellationToken).ConfigureAwait(false);
            return new AttemptResult(reason, LastKnownInCase, ChannelBusy: false, Succeeded: true);
        }
        catch (ChannelBusyException ex)
        {
            // Never retry-storm a busy channel: something else legitimately holds it.
            _attempt = Backoff.Count - 1;
            _log.Note($"channel busy: {ex.Message}");
            return new AttemptResult(DisconnectReason.ChannelBusy, InCase: false, ChannelBusy: true, Succeeded: false);
        }
        catch (OperationCanceledException)
        {
            return new AttemptResult(DisconnectReason.Requested, InCase: false, ChannelBusy: false, Succeeded: false);
        }
        catch (Exception ex)
        {
            // Deliberately does not advance the rung here. _attempt is the index of the
            // rung to use *now*; advancing it before the delay is computed would skip the
            // first rung entirely and make the very first retry wait two seconds.
            _log.Note($"connect failed: {ex.GetType().Name}: {ex.Message}");
            return new AttemptResult(DisconnectReason.Error, LastKnownInCase, ChannelBusy: false, Succeeded: false);
        }
        finally
        {
            Client = null;
            _session = null;

            if (session is not null)
            {
                session.NotificationReceived -= OnNotification;

                // Disposing the session disposes the transport it was given.
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            if (transport is not null)
            {
                transport.Disconnected -= OnTransportDisconnected;
            }
        }
    }

    /// <summary>Completes when the session's receive loop ends, or when we are shutting down.</summary>
    private static async Task WaitForEndAsync(BudsSession session, CancellationToken cancellationToken)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, SessionState state)
        {
            if (state == SessionState.Disconnected)
            {
                ended.TrySetResult();
            }
        }

        session.StateChanged += OnStateChanged;
        try
        {
            if (session.State == SessionState.Disconnected)
            {
                return;
            }

            await ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a fault.
        }
        finally
        {
            session.StateChanged -= OnStateChanged;
        }
    }

    private void OnNotification(object? sender, Protocol.Framing.OpFrame frame) =>
        NotificationReceived?.Invoke(this, frame);

    /// <summary>The next rung, jittered ±20% so many clients do not resynchronise.</summary>
    private TimeSpan NextDelay(AttemptResult outcome)
    {
        if (outcome.Succeeded)
        {
            // A drop after a good connection retries promptly: the buds were there a
            // moment ago. The exception is a case cycle, which has no urgency at all.
            _attempt = outcome.InCase ? Backoff.Count - 1 : 0;
        }

        var basis = Backoff[Math.Clamp(_attempt, 0, Backoff.Count - 1)];

        if (!outcome.Succeeded)
        {
            _attempt = Math.Min(_attempt + 1, Backoff.Count - 1);
        }

        var factor = 1.0 + ((_random.NextDouble() * 0.4) - 0.2);
        return TimeSpan.FromMilliseconds(basis.TotalMilliseconds * factor);
    }

    private static string DescribeWait(AttemptResult outcome, TimeSpan wait) => outcome switch
    {
        { ChannelBusy: true } => "Control channel busy — close the earbud settings page on your phone",
        { InCase: true } => "In case",
        _ => $"Disconnected — retrying in {wait.TotalSeconds:F0}s",
    };

    private async Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        CancellationTokenSource wake;
        lock (_wakeGate)
        {
            wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _wake = wake;
        }

        try
        {
            await _delay(duration, wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either shutdown or an explicit wake-up. Both mean "stop waiting".
        }
        finally
        {
            lock (_wakeGate)
            {
                _wake = null;
            }

            wake.Dispose();
        }
    }

    private void Publish(SupervisorStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        RequestReconnectNow();

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }
}
