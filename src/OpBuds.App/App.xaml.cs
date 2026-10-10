using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpBuds.App.Services;
using OpBuds.App.ViewModels;
using OpBuds.App.Views;
using OpBuds.App.Views.Pages;

namespace OpBuds.App;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _instance;
    private TrayService? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Claimed before anything else: a second process would not be a duplicate window,
        // it would be a second claimant on a control channel that is very likely
        // single-session, and it would lose.
        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }

        _instance.ShowRequested += (_, _) => Dispatcher.Invoke(ShowMainWindow);

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var settings = new SettingsStore();

        // Must happen before any window exists: the render mode is fixed for the process.
        if (settings.Current.SoftwareRendering)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        ThemeSupport.Apply();

        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<AutostartService>();

        // The controller marshals its own notifications, so nothing downstream needs to
        // know a Dispatcher exists.
        builder.Services.AddSingleton(_ => new BudsController(
            settings, action => Dispatcher.BeginInvoke(action)));

        builder.Services.AddSingleton<TrayService>();
        builder.Services.AddHostedService<BudsHostedService>();

        // One selector shared by both pages and the tray, so they cannot disagree about
        // which noise-control mode is active or which strength was last chosen.
        builder.Services.AddSingleton<AncSelectorViewModel>();

        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton<SoundViewModel>();
        builder.Services.AddSingleton<ControlsViewModel>();
        builder.Services.AddSingleton<DeviceViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<DeveloperViewModel>();

        builder.Services.AddSingleton<MainWindow>();

        // Singletons: the window holds all six for its lifetime, so each keeps its scroll
        // position and bindings between visits instead of being rebuilt on every click.
        builder.Services.AddSingleton<DashboardPage>();
        builder.Services.AddSingleton<SoundPage>();
        builder.Services.AddSingleton<ControlsPage>();
        builder.Services.AddSingleton<DevicePage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddSingleton<DeveloperPage>();

        _host = builder.Build();
        _host.Start();

        // Installs that turned autostart on through the Run key move onto the logon task.
        // Explorer silently skipped that entry on the machine this was built on; see
        // AutostartService for the evidence.
        _host.Services.GetRequiredService<AutostartService>().MigrateFromRunKey();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;

        var startHidden = e.Args.Contains(AutostartService.MinimizedArgument)
                          || settings.Current.StartMinimized;

        if (!startHidden)
        {
            window.Show();
        }

        // EnsureHandle creates the HWND without showing anything, which is what the tray
        // needs on the autostart path where no window is ever shown. It must run *after*
        // Show on the normal path: creating the handle first leaves the window's content
        // unattached, and it comes up blank -- laid out correctly, on screen, painting
        // nothing at all.
        var handle = new WindowInteropHelper(window).EnsureHandle();

        _tray = _host.Services.GetRequiredService<TrayService>();
        _tray.Attach(handle);
        _tray.ShowRequested += (_, _) => ShowMainWindow();
        _tray.ExitRequested += (_, _) => Shutdown();

        // Never leave the user with no way to reach the app. With no tray icon, "start
        // minimised" would mean a running process with no visible surface and no way to quit
        // it short of Task Manager. But at sign-in a refused icon usually just means the
        // taskbar does not exist yet, and the icon keeps being offered for a minute — so the
        // window is the fallback only if that minute passes with no icon.
        if (startHidden && !_tray.IsRegistered)
        {
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            fallback.Tick += (_, _) =>
            {
                fallback.Stop();
                if (!_tray.IsRegistered)
                {
                    window.Show();
                }
            };
            fallback.Start();
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();

        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
    }

    /// <summary>
    /// Keeps a background fault from silently killing a tray app.
    /// </summary>
    /// <remarks>
    /// With no window on screen there is nothing to show a crash, so the default behaviour
    /// is the process vanishing and the earbuds quietly losing their controller.
    /// </remarks>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // The whole chain, not just the outer message. A XamlParseException's own message
        // names the property that failed and nothing about why — the cause is always one or
        // more levels down, and reporting only the top turns a two-minute fix into a hunt.
        var detail = new System.Text.StringBuilder();
        for (Exception? error = e.Exception; error is not null; error = error.InnerException)
        {
            detail.AppendLine($"{error.GetType().Name}: {error.Message}");
        }

        detail.AppendLine().AppendLine(e.Exception.ToString());

        try
        {
            System.IO.Directory.CreateDirectory(SettingsStore.LogDirectory);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(
                    SettingsStore.LogDirectory,
                    $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                detail.ToString());
        }
        catch (System.IO.IOException)
        {
            // Reporting the crash must not cause one.
        }

        MessageBox.Show(
            detail.ToString(),
            "OpBuds hit an error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();

        if (_host is not null)
        {
            // Two seconds is enough to close a socket and is short enough that nobody waits
            // on a tray "Exit" wondering whether it worked.
            _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            _host.Dispose();
        }

        _instance?.Dispose();
        base.OnExit(e);
    }
}
