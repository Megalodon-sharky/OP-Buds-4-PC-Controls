using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpBuds.App.Services;
using OpBuds.Protocol.Model;
using OpBuds.Protocol.Profiles;
using Wpf.Ui.Appearance;

namespace OpBuds.App.ViewModels;

/// <summary>
/// What the user picks, as opposed to what the device stores.
/// </summary>
/// <remarks>
/// The firmware has seven ANC states, four of which are strengths of the same thing. The
/// phone does not offer seven buttons and neither does this: noise cancellation is one
/// choice, and its strength is a second, smaller one that only exists once it is selected.
/// </remarks>
public enum AncGroup
{
    Off,
    Transparency,
    Adaptive,
    NoiseCancelling,
}

public sealed record AncGroupOption(AncGroup Group, string Label);

public sealed record AncLevelOption(AncMode Mode, string Label);

/// <summary>One selectable EQ preset.</summary>
public sealed record EqOption(EqPreset Preset, string Label);

/// <summary>Shared option lists, so no two screens can drift apart.</summary>
public static class DeviceOptions
{
    public static IReadOnlyList<AncGroupOption> AncGroups { get; } =
    [
        new(AncGroup.Off, "Off"),
        new(AncGroup.Transparency, "Transparency"),
        new(AncGroup.Adaptive, "Adaptive"),
        new(AncGroup.NoiseCancelling, "Noise cancellation"),
    ];

    /// <remarks>All four confirmed by writing the 16-bit form and reading the mode back.</remarks>
    public static IReadOnlyList<AncLevelOption> AncLevels { get; } =
    [
        new(AncMode.NoiseCancellingAuto, "Auto"),
        new(AncMode.NoiseCancellingLow, "Low"),
        new(AncMode.NoiseCancellingModerate, "Moderate"),
        new(AncMode.NoiseCancellingHigh, "High"),
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

    public static bool IsLevel(AncMode mode) =>
        AncLevels.Any(level => level.Mode == mode);

    public static AncGroup GroupOf(AncMode? mode) => mode switch
    {
        AncMode.Transparency => AncGroup.Transparency,
        AncMode.Adaptive => AncGroup.Adaptive,
        not null when IsLevel(mode.Value) => AncGroup.NoiseCancelling,
        _ => AncGroup.Off,
    };
}

/// <summary>
/// The noise-control picker: four choices, plus a strength dropdown that only exists when
/// noise cancellation is the choice.
/// </summary>
/// <remarks>
/// One instance is shared by every screen and the tray, so the window and the tray menu
/// cannot disagree about what is selected.
/// </remarks>
public sealed partial class AncSelectorViewModel : ObservableObject
{
    /// <summary>
    /// The strength to restore when noise cancellation is re-selected.
    /// </summary>
    /// <remarks>
    /// Without this, picking "Noise cancellation" would have to guess a strength every
    /// time, and each guess is an audible switch.
    /// </remarks>
    private AncMode _preferredLevel = AncMode.NoiseCancellingHigh;

    public AncSelectorViewModel(BudsController buds)
    {
        Buds = buds;
        buds.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BudsController.CurrentAnc))
            {
                return;
            }

            if (buds.CurrentAnc is { } mode && DeviceOptions.IsLevel(mode))
            {
                _preferredLevel = mode;
            }

            OnPropertyChanged(nameof(CurrentGroup));
            OnPropertyChanged(nameof(IsNoiseCancelling));
            OnPropertyChanged(nameof(SelectedLevel));
        };
    }

    public BudsController Buds { get; }

    public IReadOnlyList<AncGroupOption> Groups => DeviceOptions.AncGroups;

    public IReadOnlyList<AncLevelOption> Levels => DeviceOptions.AncLevels;

    public AncGroup CurrentGroup => DeviceOptions.GroupOf(Buds.CurrentAnc);

    public bool IsNoiseCancelling => CurrentGroup == AncGroup.NoiseCancelling;

    /// <summary>Two-way for the dropdown. Setting it writes to the device.</summary>
    public AncLevelOption? SelectedLevel
    {
        get
        {
            var mode = Buds.CurrentAnc is { } current && DeviceOptions.IsLevel(current)
                ? current
                : _preferredLevel;

            return Levels.FirstOrDefault(level => level.Mode == mode);
        }

        set
        {
            if (value is null || value.Mode == Buds.CurrentAnc)
            {
                return;
            }

            _preferredLevel = value.Mode;
            _ = Buds.SetAncAsync(value.Mode);
        }
    }

    [RelayCommand]
    public Task SetGroupAsync(AncGroup group) => Buds.SetAncAsync(group switch
    {
        AncGroup.Transparency => AncMode.Transparency,
        AncGroup.Adaptive => AncMode.Adaptive,
        AncGroup.NoiseCancelling => _preferredLevel,
        _ => AncMode.Off,
    });
}

/// <summary>Connection, battery, wear, noise control, and find.</summary>
public sealed partial class DashboardViewModel(BudsController buds, AncSelectorViewModel anc) : ObservableObject
{
    public BudsController Buds { get; } = buds;

    public AncSelectorViewModel Anc { get; } = anc;

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

/// <summary>Noise control, EQ presets, BassWave and any saved custom profiles.</summary>
public sealed partial class SoundViewModel(BudsController buds, AncSelectorViewModel anc) : ObservableObject
{
    public BudsController Buds { get; } = buds;

    public AncSelectorViewModel Anc { get; } = anc;

    public IReadOnlyList<EqOption> EqOptions => DeviceOptions.Eq;

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
    /// Buds Pro 3 guesses that have never been transmitted to this hardware. The project rule
    /// is that the <c>0x04xx</c> space is never fuzzed, because an unknown setter could be a
    /// factory reset or DFU entry. So these read, and only read.
    /// </remarks>
    public string ReadOnlyReason =>
        "Read-only. These setter command ids are still inherited from the Buds Pro 3 and " +
        "have never been sent to this hardware — an unverified command in the 0x04xx space " +
        "could be a factory reset, so it is not guessed at.";
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
    }

    public BudsController Buds { get; }

    public string SettingsPath => _store.FilePath;

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

/// <summary>
/// Puts the app in dark mode and keeps it there.
/// </summary>
/// <remarks>
/// <para>
/// Dark only, on purpose. The app's text is literal white, so a light surface would make
/// every label invisible — which is exactly what happened when the theme was allowed to
/// follow the system: the window laid out perfectly and painted nothing.
/// </para>
/// <para>
/// Following the system properly means recolouring text with the theme, and WPF-UI rewrites
/// the very brushes that would carry that colour whenever its theme manager runs. Until
/// that is worth solving, one theme that always works beats two that sometimes do.
/// </para>
/// </remarks>
public static class ThemeSupport
{
    public static void Apply() => ApplicationThemeManager.Apply(ApplicationTheme.Dark);
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
        value is int percent ? $"{percent}" : "—";

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
