using System.Windows.Controls;
using OpBuds.App.ViewModels;

namespace OpBuds.App.Views.Pages;

/*
 * UserControls, not Pages. A Page implies a Frame, and a Frame measures its content with
 * unbounded height -- which is exactly why nothing scrolled. These are held by the window
 * and swapped in a ContentControl, so each gets a real height and keeps its scroll
 * position between visits.
 *
 * All six code-behinds live in one file on purpose: each is a constructor that assigns a
 * view model and calls InitializeComponent, and six files saying the same four lines would
 * be six places to look and nothing more to learn.
 */

public partial class DashboardPage : UserControl
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class SoundPage : UserControl
{
    public SoundPage(SoundViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class ControlsPage : UserControl
{
    public ControlsPage(ControlsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class DevicePage : UserControl
{
    public DevicePage(DeviceViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class SettingsPage : UserControl
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class DeveloperPage : UserControl
{
    public DeveloperPage(DeveloperViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
