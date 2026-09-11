using OpBuds.Protocol.Model;

namespace OpBuds.Core.Session;

/// <summary>How often to ask, driven by how visible the answer is.</summary>
public enum PollTier
{
    /// <summary>Paused. Nothing is asked at all.</summary>
    Paused,

    /// <summary>A window is on screen showing the numbers.</summary>
    WindowVisible,

    /// <summary>Only the tray tooltip can show them.</summary>
    TrayOnly,

    /// <summary>Nobody is looking.</summary>
    Idle,
}

/// <summary>One poll's worth of device state.</summary>
public sealed record DeviceReadout(
    IReadOnlyList<BatteryLevel> Battery,
    IReadOnlyList<WearState> Wear,
    DateTimeOffset TakenAt)
{
    public static readonly DeviceReadout Empty = new([], [], DateTimeOffset.MinValue);

    /// <summary>True when every bud that is present is sitting in the case.</summary>
    /// <remarks>
    /// Feeds <see cref="ConnectionSupervisor.LastKnownInCase"/>, which is the only evidence
    /// available for telling an expected disconnect from a real one.
    /// </remarks>
    public bool AllInCase =>
        Wear.Count > 0 &&
        Wear.Where(state => state.Side != BudSide.Case).All(state => !state.Present || state.InCase);
}

/// <summary>
/// Schedules battery and wear reads, because neither is ever pushed.
/// </summary>
/// <remarks>
/// <para>
/// Wear is read alongside battery even though the plan called this a battery poller. Wear
/// <i>changes</i> do arrive as <c>0x0204</c> notifications, but that family's payload is not
/// decoded yet — so the honest options were to poll it or to show stale wear state, and one
/// extra request per cycle is cheaper than either.
/// </para>
/// <para>
/// This owns <b>when</b> to read, never <b>how</b>: the read itself is supplied by the caller.
/// That split is not stylistic. <see cref="BudsSession"/> throws when two requests for one
/// command id overlap, so every device access in a process has to funnel through a single
/// gate — and that gate belongs to whoever owns the client, not to a timer.
/// </para>
/// <para>
/// Never more than one poll in flight either: a tick that lands while the previous one is
/// still waiting on the radio is dropped, not queued. Queueing them would turn one slow reply
/// into a growing backlog of requests for a value that is about to be superseded anyway.
/// </para>
/// </remarks>
public sealed class BatteryPoller : IAsyncDisposable
{
    public static readonly IReadOnlyDictionary<PollTier, TimeSpan> Intervals =
        new Dictionary<PollTier, TimeSpan>
        {
            [PollTier.WindowVisible] = TimeSpan.FromSeconds(5),
            [PollTier.TrayOnly] = TimeSpan.FromSeconds(30),
            [PollTier.Idle] = TimeSpan.FromSeconds(120),

            // Paused still ticks, slowly, so that un-pausing does not depend on anyone
            // remembering to restart a timer. The tick itself does nothing.
            [PollTier.Paused] = TimeSpan.FromSeconds(120),
        };

    private readonly Func<CancellationToken, Task<DeviceReadout?>> _read;
    private readonly SemaphoreSlim _inFlight = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicTimer _timer;

    private Task? _loop;
    private PollTier _tier;

    /// <param name="read">
    /// Performs one read, or returns null when there is nothing to read from — disconnected,
    /// or the request timed out. Must serialise itself against every other device access.
    /// </param>
    /// <param name="initialTier">Starting cadence.</param>
    public BatteryPoller(
        Func<CancellationToken, Task<DeviceReadout?>> read,
        PollTier initialTier = PollTier.TrayOnly)
    {
        _read = read;
        _tier = initialTier;
        _timer = new PeriodicTimer(Intervals[initialTier]);
    }

    /// <summary>The most recent successful readout.</summary>
    public DeviceReadout Latest { get; private set; } = DeviceReadout.Empty;

    public event EventHandler<DeviceReadout>? Updated;

    /// <summary>
    /// How often to poll. Setting it re-times the running timer immediately.
    /// </summary>
    /// <remarks>
    /// <see cref="PeriodicTimer.Period"/> became settable in .NET 8, so the tier can change
    /// without tearing down and rebuilding the timer — which would otherwise lose the phase
    /// and could fire twice in quick succession.
    /// </remarks>
    public PollTier Tier
    {
        get => _tier;
        set
        {
            if (_tier == value)
            {
                return;
            }

            _tier = value;
            _timer.Period = Intervals[value];
        }
    }

    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("the poller is already running");
        }

        _loop = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    }

    /// <summary>
    /// Polls now, regardless of tier. Returns false if a poll was already running.
    /// </summary>
    /// <remarks>
    /// Called on connect, on a wear change, when the tray menu opens, and on resume — every
    /// moment where the displayed number is about to be looked at.
    /// </remarks>
    public Task<bool> ForceAsync(CancellationToken cancellationToken = default) =>
        PollAsync(cancellationToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_tier == PollTier.Paused)
                {
                    continue;
                }

                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
    }

    private async Task<bool> PollAsync(CancellationToken cancellationToken)
    {
        if (!await _inFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            // Null means the link is going or gone. The supervisor owns that decision, so
            // this reports nothing rather than inventing a zero reading.
            if (await _read(cancellationToken).ConfigureAwait(false) is not { } readout)
            {
                return false;
            }

            Latest = readout;
            Updated?.Invoke(this, Latest);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // A poll racing a disconnect is ordinary. The supervisor will notice.
            return false;
        }
        finally
        {
            _inFlight.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _timer.Dispose();

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

        _lifetime.Dispose();
        _inFlight.Dispose();
    }
}
