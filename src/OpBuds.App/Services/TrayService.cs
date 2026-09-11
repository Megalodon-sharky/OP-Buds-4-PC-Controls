using System.ComponentModel;
using System.Windows.Controls;
using OpBuds.App.ViewModels;
using OpBuds.Protocol.Model;

namespace OpBuds.App.Services;

/// <summary>
/// The tray icon: the app's real front door.
/// </summary>
/// <remarks>
/// <para>
/// Built in code rather than declared in the window's XAML, because the window is often
/// never shown. A tray icon that registers from <c>Loaded</c> never registers at all when
/// the app autostarts straight into the notification area.
/// </para>
/// <para>
/// The menu is shaped like the pages: four noise-control choices, with the four strengths
/// tucked into a submenu under the one they belong to. It reads the same shared selector
/// view model the window does, so the two cannot disagree about what is selected.
/// </para>
/// </remarks>
public sealed class TrayService(
    BudsController buds,
    AncSelectorViewModel anc,
    DashboardViewModel dashboard) : IDisposable
{
    private readonly TrayIcon _icon = new();
    private readonly Dictionary<AncGroup, MenuItem> _groupItems = [];
    private readonly Dictionary<AncMode, MenuItem> _levelItems = [];

    private MenuItem? _statusItem;

    public event EventHandler? ShowRequested;

    public event EventHandler? ExitRequested;

    /// <summary>False when Windows refused the icon. The app must then show a window.</summary>
    public bool IsRegistered => _icon.IsRegistered;

    public void Attach(IntPtr parentHandle)
    {
        _icon.Menu = BuildMenu();
        _icon.LeftClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        if (!_icon.Register(parentHandle, buds.TrayTooltip))
        {
            return;
        }

        buds.PropertyChanged += OnBudsPropertyChanged;
        Refresh();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        _statusItem = new MenuItem { Header = buds.StatusText, IsEnabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());

        foreach (var option in anc.Groups)
        {
            var item = new MenuItem { Header = option.Label, IsCheckable = true };
            var group = option.Group;

            if (group == AncGroup.NoiseCancelling)
            {
                // Its strengths live under it rather than beside it, so the top level
                // stays four choices instead of seven.
                foreach (var level in anc.Levels)
                {
                    var mode = level.Mode;
                    var child = new MenuItem { Header = level.Label, IsCheckable = true };

                    child.Click += async (_, _) =>
                    {
                        await buds.SetAncAsync(mode);
                        Refresh();
                    };

                    _levelItems[mode] = child;
                    item.Items.Add(child);
                }
            }
            else
            {
                item.Click += async (_, _) =>
                {
                    await anc.SetGroupAsync(group);
                    Refresh();
                };
            }

            _groupItems[group] = item;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var find = new MenuItem { Header = "Find my earbuds…" };
        find.Click += (_, _) => dashboard.FindCommand.Execute(null);
        menu.Items.Add(find);

        var open = new MenuItem { Header = "Open", FontWeight = System.Windows.FontWeights.SemiBold };
        open.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(open);

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exit);

        // The tooltip and the icon are the only places battery shows while the window is
        // hidden, so a refresh is forced the moment someone goes looking for it.
        menu.Opened += async (_, _) => await buds.RefreshBatteryAsync();

        return menu;
    }

    private void OnBudsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BudsController.TrayTooltip)
            or nameof(BudsController.LowestBudPercent)
            or nameof(BudsController.CurrentAnc)
            or nameof(BudsController.StatusText)
            or nameof(BudsController.IsConnected))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        _icon.SetTooltip(buds.TrayTooltip);
        _icon.SetBattery(buds.IsConnected ? buds.LowestBudPercent : null);

        if (_statusItem is not null)
        {
            _statusItem.Header = buds.StatusText;
        }

        foreach (var (group, item) in _groupItems)
        {
            item.IsChecked = anc.CurrentGroup == group;
            item.IsEnabled = buds.IsConnected;
        }

        foreach (var (mode, item) in _levelItems)
        {
            item.IsChecked = buds.CurrentAnc == mode;
        }
    }

    public void Dispose()
    {
        buds.PropertyChanged -= OnBudsPropertyChanged;
        _icon.Dispose();
    }
}
