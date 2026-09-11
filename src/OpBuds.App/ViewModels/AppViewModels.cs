using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpBuds.App.Services;
using OpBuds.Protocol.Model;
using OpBuds.Protocol.Profiles;

namespace OpBuds.App.ViewModels;

/// <summary>One selectable ANC mode, with the label the phone's UI would use.</summary>
public sealed record AncOption(AncMode Mode, string Label);

/// <summary>One selectable EQ preset.</summary>
public sealed record EqOption(EqPreset Preset, string Label);

/// <summary>
/// Shared option lists, so the dashboard's compact picker and the sound page's full list
/// cannot drift apart.
/// </summary>
public static class DeviceOptions
{
    /// <remarks>
    /// All seven are confirmed by write-and-read-back. The phone's single "Noise
    /// Cancellation" button maps to <see cref="AncMode.NoiseCancellingHigh"/>.
    /// </remarks>
    public static IReadOnlyList<AncOption> Anc { get; } =
    [
        new(AncMode.Off, "Off"),
        new(AncMode.Transparency, "Transparency"),
        new(AncMode.Adaptive, "Adaptive"),
        new(AncMode.NoiseCancellingHigh, "Noise cancelling — high"),
        new(AncMode.NoiseCancellingModerate, "Noise cancelling — moderate"),
        new(AncMode.NoiseCancellingLow, "Noise cancelling — low"),
        new(AncMode.NoiseCancellingAuto, "Noise cancelling — auto"),
    ];

    /// <remarks>
    /// Only the four the phone selects. The firmware accepts <c>0x03</c> and
    /// <c>0x05</c>-<c>0x07</c> without complaint and no audible effect has been confirmed
    /// for them, so they are not offered.
    /// </remarks>
    public static IReadOnlyList<EqOption> Eq { get; } =
    [
        new(EqPreset.Balanced, "Balanced"),
        new(EqPreset.Serenade, "Serenade"),
        new(EqPreset.Bass, "Bass"),
        new(EqPreset.Custom, "Custom profile"),
    ];
}

/// <summary>Connection, battery, wear, quick ANC, and find.</summary>
public sealed partial class DashboardViewModel(BudsController buds) : ObservableObject
{
    public BudsController Buds { get; } = buds;

    public IReadOnlyList<AncOption> AncOptions => DeviceOptions.Anc;

    [RelayCommand]
    private Task SetAncAsync(AncMode mode) => Buds.SetAncAsync(mode);

    [RelayCommand]
    private Task RefreshAsync() => Buds.RefreshBatteryAsync();

    /// <summary>
    /// Rings the earbuds, after asking.
    /// </summary>
    /// <remarks>
    /// The prompt is not politeness. The locate tone is loud, it plays in whichever bud is
    /// selected, and <b>there is no stop command</b> — it ends when the firmware decides it
    /// ends. Anyone wearing them wants to know first.
    /// </remarks>
    [RelayCommand]
    private async Task FindAsync()
    {
        var answer = MessageBox.Show(
            "This rings the earbuds at locate volume.\n\n" +
            "Take them out of your ears first. The tone stops on its own — there is no " +
            "stop command.",
            "Find my earbuds",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.OK)
        {
            await Buds.FindAsync(FindTarget.Both);
        }
    }
}

/// <summary>ANC in full, EQ presets, BassWave and any saved custom profiles.</summary>
public sealed partial class SoundViewModel(BudsController buds) : ObservableObject
{
    public BudsController Buds { get; } = buds;

    public IReadOnlyList<AncOption> AncOptions => DeviceOptions.Anc;

    public IReadOnlyList<EqOption> EqOptions => DeviceOptions.Eq;

    [RelayCommand]
    private Task SetAncAsync(AncMode mode) => Buds.SetAncAsync(mode);

    [RelayCommand]
    private Task SetEqAsync(EqPreset preset) => Buds.SetEqPresetAsync(preset);
}

/// <summary>Touch bindings and misc feature switches — read-only, deliberately.</summary>
public sealed class ControlsViewModel(BudsController buds)
{
    public BudsController Buds { get; } = buds;

    /// <summary>
    /// Why nothing on this page can be changed yet.
    /// </summary>
    /// <remarks>
    /// The setter command ids for touch, custom EQ and the misc switches are still inherited
    /// Buds Pro 3 guesses and have never been transmitted to this hardware. The project rule
    /// is that the <c>0x04xx</c> space is never fuzzed, because an unknown setter could be a
    /// factory reset or DFU entry. So these read, and only read.
    /// </remarks>
    public string ReadOnlyReason =>
        "These are read-only. Their setter command ids are still inherited from the Buds " +
        "Pro 3 and have never been sent to this hardware — and an unverified command in the " +
        "0x04xx space could be a factory reset or a firmware-update trigger, so it is not " +
        "guessed at.";

    /// <summary>The one misc id whose meaning is actually known.</summary>
    public static string DescribeMisc(MiscFeature feature) => feature.Id switch
    {
        0x1D => $"0x{feature.Id:X2}  BassWave",
        _ => $"0x{feature.Id:X2}  (unnamed)",
    };
}

/// <summary>Identity, firmware, peers, and what is deliberately absent.</summary>
public sealed partial class DeviceViewModel(BudsController buds) : ObservableObject
{
    public BudsController Buds { get; } = buds;

    /// <summary>
    /// Hi-res / LHDC is shown as status, never as a control.
    /// </summary>
    /// <remarks>
    /// On Windows the A2DP codec lives in the Microsoft Bluetooth audio stack, which offers
    /// SBC and AAC only — no driver or adapter change enables LHDC. Toggling it also dropped
    /// the control connection twice during testing. A control that disconnects the app and
    /// changes nothing audible is worse than no control.
    /// </remarks>
    public string HiResNote =>
        "Hi-res (LHDC) is a real setting on the earbuds, but Windows cannot use the codec: " +
        "the Microsoft Bluetooth audio stack offers SBC and AAC only. Shown here as status " +
        "for that reason.";

    /// <summary>Capabilities excluded on purpose, with the reason, straight from the profile.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Excluded { get; } =
        [.. FeatureSet.Excluded];

    [RelayCommand]
    private void Reconnect() => Buds.ReconnectNow();

    [RelayCommand]
    private Task RefreshAsync() => Buds.RefreshAllAsync();
}

/// <summary>Preferences, and the autostart toggle that tells the truth.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AutostartService _autostart;

    public SettingsViewModel(SettingsStore store, AutostartService autostart, BudsController buds)
    {
        _store = store;
        _autostart = autostart;
        Buds = buds;

        // Read the registry, not the settings file: the user can remove the Run entry from
        // Task Manager and the file would never hear about it.
        _startWithWindows = autostart.IsEnabled;
        _startMinimized = store.Current.StartMinimized;
        _closeToTray = store.Current.CloseToTray;
        _protocolLogging = store.Current.ProtocolLogging;
        _pollSecondsVisible = store.Current.PollSecondsVisible;
        _deviceMac = store.Current.DeviceMac;
        _theme = store.Current.Theme;
    }

    public BudsController Buds { get; }

    public string SettingsPath => _store.FilePath;

    public static string LogDirectory => SettingsStore.LogDirectory;

    public IReadOnlyList<string> Themes { get; } = ["system", "light", "dark"];

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _protocolLogging;

    [ObservableProperty]
    private int _pollSecondsVisible;

    [ObservableProperty]
    private string _deviceMac;

    [ObservableProperty]
    private string _theme;

    /// <summary>Set when a change needs a restart to take effect, so the UI can say so.</summary>
    [ObservableProperty]
    private bool _restartRequired;

    partial void OnStartWithWindowsChanged(bool value)
    {
        // Ask the registry what actually happened rather than assuming the write worked;
        // group policy can lock the Run key down entirely.
        var actual = _autostart.Set(value);
        _store.Update(settings => settings with { StartWithWindows = actual });

        if (actual != value)
        {
            StartWithWindows = actual;
        }
    }

    partial void OnStartMinimizedChanged(bool value) =>
        _store.Update(settings => settings with { StartMinimized = value });

    partial void OnCloseToTrayChanged(bool value) =>
        _store.Update(settings => settings with { CloseToTray = value });

    partial void OnPollSecondsVisibleChanged(int value) =>
        _store.Update(settings => settings with { PollSecondsVisible = Math.Clamp(value, 2, 60) });

    partial void OnProtocolLoggingChanged(bool value)
    {
        _store.Update(settings => settings with { ProtocolLogging = value });

        // The log is opened once, when the controller is built. Turning capture on mid-run
        // would need a second file handle and a second truth about where frames go.
        RestartRequired = true;
    }

    partial void OnDeviceMacChanged(string value)
    {
        _store.Update(settings => settings with { DeviceMac = value.Trim() });
        RestartRequired = true;
    }

    partial void OnThemeChanged(string value)
    {
        _store.Update(settings => settings with { Theme = value });
        ThemeSupport.Apply(value);
    }

    [RelayCommand]
    private void OpenSettingsFolder() => Reveal(SettingsStore.DefaultDirectory);

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(SettingsStore.LogDirectory);
        Reveal(SettingsStore.LogDirectory);
    }

    private static void Reveal(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // Explorer is missing or blocked. Not worth an error dialog.
        }
    }
}

/// <summary>Live protocol traffic. Not a user feature — a bring-up feature.</summary>
public sealed partial class DeveloperViewModel(BudsController buds) : ObservableObject
{
    public BudsController Buds { get; } = buds;

    [RelayCommand]
    private async Task ExportAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"opbuds-traffic-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Text|*.txt|All files|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            await Buds.ExportTrafficAsync(dialog.FileName);
        }
    }

    [RelayCommand]
    private void Clear() => Buds.Traffic.Clear();
}

/// <summary>Applies the stored theme choice.</summary>
public static class ThemeSupport
{
    public static void Apply(string theme)
    {
        switch (theme)
        {
            case "light":
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                    Wpf.Ui.Appearance.ApplicationTheme.Light);
                break;
            case "dark":
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                    Wpf.Ui.Appearance.ApplicationTheme.Dark);
                break;
            default:
                Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
                break;
        }
    }
}

/// <summary>True when the bound value equals the converter parameter. Drives radio groups.</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// True when both bound values are equal. Drives radio groups over an enum.
/// </summary>
/// <remarks>
/// A <c>MultiBinding</c> rather than a converter parameter because the value being compared
/// against comes from the item being templated, and <c>ConverterParameter</c> is not a
/// dependency property, so it cannot be bound.
/// </remarks>
public sealed class EqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("one-way only");
}

/// <summary>True when the bound value is a non-empty string or a non-null object.</summary>
public sealed class HasValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.Length > 0 : value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Renders a percentage, or an em dash when there is no reading.
/// </summary>
/// <remarks>
/// A dedicated converter rather than <c>StringFormat</c> chained after
/// <see cref="NullDashConverter"/>, which would render a missing reading as "—%".
/// </remarks>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int percent ? $"{percent}%" : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Renders null as an em dash rather than as blank space.</summary>
public sealed class NullDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() is { Length: > 0 } text ? text : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
