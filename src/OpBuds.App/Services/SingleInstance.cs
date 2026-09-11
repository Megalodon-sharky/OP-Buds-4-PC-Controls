using System.Threading;

namespace OpBuds.App.Services;

/// <summary>
/// One app, one control channel.
/// </summary>
/// <remarks>
/// <para>
/// Not merely tidy: the vendor RFCOMM channel is very likely single-session, so a second
/// instance would not be a duplicate window — it would be a second process fighting the
/// first for the radio and losing.
/// </para>
/// <para>
/// Names are <c>Local\</c>-scoped, so two different Windows users on the same machine each
/// get their own instance, which is correct — they have their own buds and their own tray.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\OpBudsControls.Instance";
    private const string SignalName = @"Local\OpBudsControls.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Thread _listener;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);

        _listener = new Thread(ListenForShowRequests)
        {
            IsBackground = true,
            Name = "opbuds-single-instance",
        };
        _listener.Start();
    }

    /// <summary>Raised on a background thread when another instance asks to be shown.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>
    /// Claims the instance, or returns null if one is already running.
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Asks the running instance to show its window. Safe when nothing is listening.</summary>
    public static void SignalExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(SignalName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is shutting down. Nothing to show.
        }
    }

    private void ListenForShowRequests()
    {
        var handles = new WaitHandle[] { _signal, _lifetime.Token.WaitHandle };

        while (WaitHandle.WaitAny(handles) == 0)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();

        if (_listener.IsAlive)
        {
            _listener.Join(TimeSpan.FromSeconds(1));
        }

        _signal.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _lifetime.Dispose();
    }
}
