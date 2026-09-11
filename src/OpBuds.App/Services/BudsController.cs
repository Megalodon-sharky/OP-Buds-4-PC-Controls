using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using OpBuds.Core.Diagnostics;
using OpBuds.Core.Session;
using OpBuds.Core.Transport;
using OpBuds.Protocol.Framing;
using OpBuds.Protocol.Model;
using OpBuds.Protocol.Profiles;

namespace OpBuds.App.Services;

/// <summary>
/// The app's single point of contact with the earbuds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every device access in the process goes through one semaphore.</b>
/// <see cref="BudsSession"/> deliberately throws when two requests for the same command id
/// overlap, which turns a background poll landing on top of a user-initiated read into a hard
/// fault rather than a slow response. One gate, held for the length of a logical operation,
/// removes that class of bug entirely — and this protocol's traffic levels make the
/// serialisation free.
/// </para>
/// <para>
/// Observable state is raised through a posted callback rather than directly, so view models
/// can bind without any of the supervisor's or poller's background threads knowing what a
/// Dispatcher is.
/// </para>
/// </remarks>
public sealed partial class BudsController : ObservableObject, IAsyncDisposable
{
    /// <summary>Rows kept in the live traffic view. Older ones are dropped, not paged.</summary>
    private const int MaxTrafficRows = 400;

    private readonly SettingsStore _settings;
    private readonly Action<Action> _post;
    private readonly IDeviceProfile _profile = Buds4Profile.Instance;
    private readonly ProtocolLog _log;
    private readonly ConnectionSupervisor _supervisor;
    private readonly BatteryPoller _poller;
    private readonly SemaphoreSlim _device = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private int _refreshQueued;

    public BudsController(SettingsStore settings, Action<Action>? post = null)
    {
        _settings = settings;
        _post = post ?? (action => action());

        var mac = settings.Current.DeviceMac;

        // A file log and the live view are the same log: two logging paths would drift the
        // moment either changed. With file logging off the sink still raises events.
        _log = settings.Current.ProtocolLogging
            ? ProtocolLog.Create(SettingsStore.LogDirectory, mac, "app")
            : ProtocolLog.Sink();

        _log.Recorded += OnRecorded;

        _supervisor = new ConnectionSupervisor(
            () => new RawBthSocketTransport(mac, _profile.ServiceUuid),
            _profile,
            _log);

        _supervisor.StatusChanged += OnStatusChanged;
        _supervisor.Connected += OnConnected;
        _supervisor.NotificationReceived += OnNotification;

        _poller = new BatteryPoller(ReadBatteryAndWearAsync);
        _poller.Updated += OnReadout;
    }

    /// <summary>Live protocol traffic, newest last. Bound by the Developer page.</summary>
    public ObservableCollection<ProtocolRecord> Traffic { get; } = [];

    public string DeviceName => _profile.Name;

    public string DeviceMac => _settings.Current.DeviceMac;

    /// <summary>The capture file, when protocol logging is on.</summary>
    public string? CapturePath => _log.Path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayTooltip))]
    private string _statusText = "Starting";

    [ObservableProperty]
    private SessionState _linkState = SessionState.Disconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayTooltip))]
    private bool _isConnected;

    /// <summary>True while a device operation is in flight, so the UI can disable writes.</summary>
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _inCase;

    [ObservableProperty]
    private bool _channelBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayTooltip))]
    private int? _leftPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayTooltip))]
    private int? _rightPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayTooltip))]
    private int? _casePercent;

    [ObservableProperty]
    private bool _leftCharging;

    [ObservableProperty]
    private bool _rightCharging;

    [ObservableProperty]
    private bool _caseCharging;

    [ObservableProperty]
    private string _wearText = "—";

    [ObservableProperty]
    private AncMode? _currentAnc;

    [ObservableProperty]
    private EqPreset? _currentEq;

    [ObservableProperty]
    private BassWaveState? _bassWave;

    [ObservableProperty]
    private string? _firmware;

    [ObservableProperty]
    private IReadOnlyList<ConnectedDevice> _peers = [];

    [ObservableProperty]
    private IReadOnlyList<MiscFeature> _miscFeatures = [];

    [ObservableProperty]
    private IReadOnlyList<TouchBinding> _touchBindings = [];

    [ObservableProperty]
    private IReadOnlyList<CustomEqProfile> _customEqProfiles = [];

    /// <summary>The last thing that went wrong, or null. Cleared by the next good connect.</summary>
    [ObservableProperty]
    private string? _lastError;

    public string TrayTooltip => IsConnected && LeftPercent is not null
        ? $"L {LeftPercent}%  ·  R {RightPercent}%  ·  Case {(CasePercent?.ToString() ?? "—")}%"
        : StatusText;

    public void Start()
    {
        _supervisor.Start();
        _poller.Start();
    }

    /// <summary>Sets how often battery is polled, from how visible the numbers are.</summary>
    public void SetPollTier(PollTier tier) => _poller.Tier = tier;

    /// <summary>Cuts short the backoff wait. Used on resume-from-sleep and by the Device page.</summary>
    public void ReconnectNow() => _supervisor.RequestReconnectNow();

    /// <summary>Polls battery and wear now, outside the normal cadence.</summary>
    public Task RefreshBatteryAsync() => _poller.ForceAsync(_lifetime.Token);

    public async Task<bool> SetAncAsync(AncMode mode)
    {
        var readback = await WithDeviceValueAsync(client => client.SetAncAsync(mode, _lifetime.Token))
            .ConfigureAwait(false);

        if (readback is null)
        {
            SetError("The earbuds did not confirm the ANC change.");
            return false;
        }

        _post(() => CurrentAnc = readback);

        // An OK ack is not proof, so this reports what the device says it is now rather
        // than what was asked for.
        return readback == mode;
    }

    public async Task<bool> SetEqPresetAsync(EqPreset preset)
    {
        var readback = await WithDeviceValueAsync(client => client.SetEqPresetAsync(preset, _lifetime.Token))
            .ConfigureAwait(false);

        if (readback is null)
        {
            SetError("The earbuds did not confirm the EQ change.");
            return false;
        }

        _post(() => CurrentEq = readback);
        return readback == preset;
    }

    /// <summary>Rings the buds. The tone self-terminates; there is no stop command.</summary>
    public async Task<bool> FindAsync(FindTarget target)
    {
        var accepted = await WithDeviceValueAsync(async client =>
                (bool?)await client.FindAsync(target, _lifetime.Token).ConfigureAwait(false))
            .ConfigureAwait(false);

        return accepted == true;
    }

    /// <summary>Reads everything the pages display. Runs once per connect.</summary>
    public async Task RefreshAllAsync()
    {
        await WithDeviceAsync<object>(async client =>
        {
            var firmware = await client.GetFirmwareAsync(_lifetime.Token).ConfigureAwait(false);
            var anc = await client.GetAncAsync(_lifetime.Token).ConfigureAwait(false);
            var eq = await client.GetEqPresetAsync(_lifetime.Token).ConfigureAwait(false);
            var bass = await client.GetBassWaveAsync(_lifetime.Token).ConfigureAwait(false);
            var custom = await client.GetCustomEqAsync(_lifetime.Token).ConfigureAwait(false);
            var touch = await client.GetTouchConfigAsync(_lifetime.Token).ConfigureAwait(false);
            var peers = await client.GetConnectedDevicesAsync(_lifetime.Token).ConfigureAwait(false);
            var misc = await client.GetMiscFeaturesAsync(_lifetime.Token).ConfigureAwait(false);

            _post(() =>
            {
                Firmware = firmware ?? Firmware;
                CurrentAnc = anc ?? CurrentAnc;
                CurrentEq = eq ?? CurrentEq;
                BassWave = bass ?? BassWave;
                CustomEqProfiles = custom ?? CustomEqProfiles;
                TouchBindings = touch?.Bindings ?? TouchBindings;
                Peers = peers ?? Peers;
                MiscFeatures = misc ?? MiscFeatures;
            });

            return null;
        }).ConfigureAwait(false);

        // Deliberately outside the gate: the poller takes it again, and SemaphoreSlim is
        // not reentrant.
        await RefreshBatteryAsync().ConfigureAwait(false);
    }

    /// <summary>Writes the visible traffic rows to a file, for pasting into a bug report.</summary>
    public async Task ExportTrafficAsync(string path)
    {
        var rows = Traffic
            .Select(row => $"{row.T,9:F3}  {row.Kind,-5} {row.Direction,-2}  {row.Summary,-28} {row.Hex}")
            .ToArray();

        await File.WriteAllLinesAsync(path, rows, _lifetime.Token).ConfigureAwait(false);
    }

    /// <summary>The poller's read. Serialised against every other device access.</summary>
    private Task<DeviceReadout?> ReadBatteryAndWearAsync(CancellationToken cancellationToken) =>
        WithDeviceAsync(async client =>
        {
            var battery = await client.GetBatteryAsync(cancellationToken).ConfigureAwait(false);
            var wear = await client.GetWearAsync(cancellationToken).ConfigureAwait(false);

            return battery is null && wear is null
                ? null
                : new DeviceReadout(
                    battery ?? _poller.Latest.Battery,
                    wear ?? _poller.Latest.Wear,
                    DateTimeOffset.Now);
        });

    /// <remarks>
    /// Two near-identical bodies rather than one generic method because C# does not let
    /// overloads differ only by their constraints, and a nullable reference and a nullable
    /// value need different ones.
    /// </remarks>
    private async Task<T?> WithDeviceAsync<T>(Func<BudsClient, Task<T?>> work)
        where T : class
    {
        if (_supervisor.Client is null)
        {
            return null;
        }

        await _device.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        _post(() => IsBusy = true);

        try
        {
            // Re-read after the wait: the link can drop while queued behind another caller.
            return _supervisor.Client is { } client
                ? await work(client).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            SetError(ex.Message);
            return null;
        }
        finally
        {
            _device.Release();
            _post(() => IsBusy = false);
        }
    }

    private async Task<T?> WithDeviceValueAsync<T>(Func<BudsClient, Task<T?>> work)
        where T : struct
    {
        if (_supervisor.Client is null)
        {
            return null;
        }

        await _device.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        _post(() => IsBusy = true);

        try
        {
            return _supervisor.Client is { } client
                ? await work(client).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            SetError(ex.Message);
            return null;
        }
        finally
        {
            _device.Release();
            _post(() => IsBusy = false);
        }
    }

    private void OnStatusChanged(object? sender, SupervisorStatus status) => _post(() =>
    {
        StatusText = status.Summary;
        LinkState = status.State;
        IsConnected = status.IsUsable;
        InCase = status.InCase;
        ChannelBusy = status.ChannelBusy;

        if (!status.IsUsable)
        {
            // Stop claiming a battery level for a device that is not there.
            LeftPercent = null;
            RightPercent = null;
            CasePercent = null;
        }
        else
        {
            LastError = null;
        }
    });

    private void OnConnected(object? sender, BudsClient client) => _ = RefreshAllAsync();

    private void OnReadout(object? sender, DeviceReadout readout) => _post(() =>
    {
        foreach (var level in readout.Battery)
        {
            switch (level.Side)
            {
                case BudSide.Left:
                    LeftPercent = level.Percent;
                    LeftCharging = level.Charging;
                    break;
                case BudSide.Right:
                    RightPercent = level.Percent;
                    RightCharging = level.Charging;
                    break;
                case BudSide.Case:
                    CasePercent = level.Percent;
                    CaseCharging = level.Charging;
                    break;
            }
        }

        if (readout.Wear.Count > 0)
        {
            WearText = string.Join("  ·  ", readout.Wear.Select(Describe));
        }

        // The only evidence available for telling a case cycle from a real disconnect.
        _supervisor.LastKnownInCase = readout.AllInCase;
    });

    private static string Describe(WearState state) => state switch
    {
        { Present: false } => $"{state.Side} absent",
        { InEar: true } => $"{state.Side} in ear",
        { InCase: true } => $"{state.Side} in case",
        _ => $"{state.Side} out",
    };

    /// <summary>
    /// Any push means something changed; a debounced re-read finds out what.
    /// </summary>
    /// <remarks>
    /// Decoding each notification family precisely would be better, and is not possible yet:
    /// the <c>0x0204</c> topic payloads are still unmapped. Re-reading is slower but it
    /// cannot be silently wrong, which matters more while the map has holes in it.
    /// </remarks>
    private void OnNotification(object? sender, OpFrame frame)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, _lifetime.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _refreshQueued, 0);
                await RefreshAllAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        });
    }

    private void OnRecorded(object? sender, ProtocolRecord record) => _post(() =>
    {
        Traffic.Add(record);

        while (Traffic.Count > MaxTrafficRows)
        {
            Traffic.RemoveAt(0);
        }
    });

    private void SetError(string message) => _post(() => LastError = message);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        _log.Recorded -= OnRecorded;
        _supervisor.StatusChanged -= OnStatusChanged;
        _supervisor.Connected -= OnConnected;
        _supervisor.NotificationReceived -= OnNotification;
        _poller.Updated -= OnReadout;

        await _poller.DisposeAsync().ConfigureAwait(false);
        await _supervisor.DisposeAsync().ConfigureAwait(false);

        _log.Dispose();
        _device.Dispose();
        _lifetime.Dispose();
    }
}
