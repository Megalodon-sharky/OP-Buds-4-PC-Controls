using System.ComponentModel;
using System.Windows;
using OpBuds.App.Services;
using OpBuds.App.Views.Pages;
using OpBuds.Core.Session;
using Wpf.Ui.Controls;

namespace OpBuds.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly BudsController _buds;
    private readonly SettingsStore _settings;

    public MainWindow(
        BudsController buds,
        SettingsStore settings,
        DashboardPage dashboard,
        SoundPage sound,
        ControlsPage controls,
        DevicePage device,
        SettingsPage settingsPage,
        DeveloperPage developer)
    {
        _buds = buds;
        _settings = settings;

        InitializeComponent();

        // Views are injected and held, not navigated to. Each one keeps its scroll position
        // and its bindings for the life of the window, and the container wires their view
        // models -- no page-provider indirection needed.
        Rail.ItemsSource = new RailItem[]
        {
            new(SymbolRegular.Home24, "Dashboard", dashboard),
            new(SymbolRegular.MusicNote124, "Sound", sound),
            new(SymbolRegular.HandDraw24, "Controls", controls),
            new(SymbolRegular.Bluetooth24, "Device", device),
            new(SymbolRegular.Settings24, "Settings", settingsPage),

            // Developer ships in v1 on purpose. It is not a user feature; it is how
            // everything else gets diagnosed while the protocol map still has holes.
            new(SymbolRegular.CodeBlock24, "Developer", developer),
        };

        Rail.SelectedIndex = 0;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>One entry in the left rail.</summary>
    public sealed record RailItem(SymbolRegular Symbol, string Label, object View);

    /// <summary>
    /// Polls faster while anyone can see the numbers, slower when only the tray can.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _buds.SetPollTier(IsVisible ? PollTier.WindowVisible : PollTier.TrayOnly);

        if (IsVisible)
        {
            // Whatever is on screen was read at the previous, slower cadence. Refresh it
            // before the user reads a number that is two minutes old.
            _ = _buds.RefreshBatteryAsync();
        }
    }

    /// <summary>
    /// Closing hides to the tray, and says so the first time.
    /// </summary>
    /// <remarks>
    /// An app that vanishes from the taskbar but keeps running is a reasonable design and a
    /// baffling surprise, so the surprise is spent once, deliberately, rather than being
    /// rediscovered in Task Manager.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_settings.Current.CloseToTray)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();

        if (!_settings.Current.CloseToTrayExplained)
        {
            _settings.Update(settings => settings with { CloseToTrayExplained = true });

            // Fully qualified: Wpf.Ui.Controls also defines a MessageBox, and this file
            // imports that namespace for FluentWindow.
            System.Windows.MessageBox.Show(
                "OpBuds is still running in the notification area, keeping the earbuds " +
                "connected.\n\nClick the tray icon to bring it back, or right-click it and " +
                "choose Exit to quit. You can turn this behaviour off in Settings.",
                "Still running",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
    }
}
