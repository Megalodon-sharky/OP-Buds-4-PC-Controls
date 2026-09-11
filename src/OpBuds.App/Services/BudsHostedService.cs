using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

namespace OpBuds.App.Services;

/// <summary>
/// Owns the earbud connection for the life of the process.
/// </summary>
/// <remarks>
/// The window comes and goes — closing to tray destroys nothing, and on autostart there may
/// never be a window at all. Tying the connection to the host rather than to a window is what
/// makes "launches to tray, already connected" possible.
/// </remarks>
public sealed class BudsHostedService(BudsController controller) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Waking from sleep, the radio comes back before anything notices the link died.
        // Cutting the backoff short here is the difference between reconnecting in a second
        // and sitting out a 30-second rung.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        controller.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        await controller.DisposeAsync().ConfigureAwait(false);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            controller.ReconnectNow();
        }
    }
}
