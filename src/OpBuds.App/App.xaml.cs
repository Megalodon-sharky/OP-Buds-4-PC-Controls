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
        ThemeSupport.Apply(settings.Current.Theme);

        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<AutostartService>();

        // The controller marshals its own notifications, so nothing downstream needs to
        // know a Dispatcher exists.
        builder.Services.AddSingleton(_ => new BudsController(
            settings, action => Dispatcher.BeginInvoke(action)));

        builder.Services.AddSingleton<TrayService>();
        builder.Services.AddHostedService<BudsHostedService>();

        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton<SoundViewModel>();
        builder.Services.AddSingleton<ControlsViewModel>();
        builder.Services.AddSingleton<DeviceViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<DeveloperViewModel>();

        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<SoundPage>();
        builder.Services.AddTransient<ControlsPage>();
        builder.Services.AddTransient<DevicePage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<DeveloperPage>();

        _host = builder.Build();
        _host.Start();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;

        // Creates the HWND without painting the window. The tray icon needs a parent
        // handle, and on autostart the window is never shown — so waiting for Loaded would
        // mean no tray icon and an app with no visible surface at all.
        var handle = new WindowInteropHelper(window).EnsureHandle();

        _tray = _host.Services.GetRequiredService<TrayService>();
        _tray.Attach(handle);
        _tray.ShowRequested += (_, _) => ShowMainWindow();
        _tray.ExitRequested += (_, _) => Shutdown();

        var startHidden = e.Args.Contains(AutostartService.MinimizedArgument)
                          || settings.Current.StartMinimized;

        // Never leave the user with no way to reach the app. If Windows refused the tray
        // icon, "start minimised" would mean a running process with no visible surface and
        // no way to quit it short of Task Manager.
        if (!startHidden || !_tray.IsRegistered)
        {
            window.Show();
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
