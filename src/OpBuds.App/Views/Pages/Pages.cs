using System.Windows.Controls;
using OpBuds.App.ViewModels;

namespace OpBuds.App.Views.Pages;

/*
 * All six code-behinds live in one file on purpose. Each is a constructor that assigns a
 * view model and calls InitializeComponent; six files saying the same four lines would be
 * six places to look and nothing more to learn.
 */

public partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class SoundPage : Page
{
    public SoundPage(SoundViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class ControlsPage : Page
{
    public ControlsPage(ControlsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class DevicePage : Page
{
    public DevicePage(DeviceViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class DeveloperPage : Page
{
    public DeveloperPage(DeveloperViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
