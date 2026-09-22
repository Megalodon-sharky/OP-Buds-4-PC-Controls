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

/// <summary>One action a gesture can be pointed at.</summary>
public sealed record TouchActionOption(byte Action, string Label);

/// <summary>One gesture the Controls page lets you re-point.</summary>
public sealed record GestureOption(byte Gesture, string Label);

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

    /// <summary>The touch context for ordinary playback. <c>0x06</c> appears to be calls.</summary>
    public const byte PlaybackContext = 0x01;

    /// <remarks>
    /// Named from the Oppo-family gesture codes Gadgetbridge documents — <c>0x0201</c>,
    /// <c>0x0301</c>, <c>0x0401</c>, little-endian <c>[context][gesture]</c> — which line up
    /// with this unit's own table. The single press (<c>0x01</c>), gestures <c>0x05</c> and
    /// <c>0x06</c>, and the call context are shown as read but not offered: what they mean
    /// on this model is unconfirmed.
    /// </remarks>
    public static IReadOnlyList<GestureOption> EditableGestures { get; } =
    [
        new(0x02, "Double press"),
        new(0x03, "Triple press"),
        new(0x04, "Press and hold"),
    ];

    /// <remarks>
    /// Only actions this unit already stores somewhere in its playback table, so a write can
    /// never hand the firmware a value it has not shown it holds. Names are the Oppo-family
    /// codes, and they match this unit's defaults: double press is previous on the left and
    /// next on the right.
    /// </remarks>
    public static IReadOnlyList<TouchActionOption> TouchActions { get; } =
    [
        new(0x00, "Nothing"),
        new(0x01, "Play / pause"),
        new(0x05, "Previous track"),
        new(0x06, "Next track"),
        new(0x03, "Voice assistant"),
        new(0x08, "Noise control"),
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
public sealed partial class SoundViewModel : ObservableObject
{
    /// <summary>How long the slider must sit still before its value is written.</summary>
    private static readonly TimeSpan BassWaveSettle = TimeSpan.FromMilliseconds(400);

    private CancellationTokenSource? _bassWaveDebounce;

    /// <summary>A level the user has chosen that the device has not yet confirmed.</summary>
    private int? _pendingBassWave;

    public SoundViewModel(BudsController buds, AncSelectorViewModel anc)
    {
        Buds = buds;
        Anc = anc;

        buds.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(BudsController.BassWave):
                    // Bounds before the level: a Slider coerces its value into its current
                    // range, and a level arriving ahead of its range would be clamped.
                    OnPropertyChanged(nameof(BassWaveMinimum));
                    OnPropertyChanged(nameof(BassWaveMaximum));
                    OnPropertyChanged(nameof(BassWaveLevel));
                    OnPropertyChanged(nameof(BassWaveText));
                    OnPropertyChanged(nameof(CanSetBassWave));
                    break;
                case nameof(BudsController.IsConnected):
                    OnPropertyChanged(nameof(CanSetBassWave));
                    break;
                case nameof(BudsController.BassWaveSwitchOn):
                    OnPropertyChanged(nameof(BassWaveNote));
                    break;
            }
        };
    }

    public BudsController Buds { get; }

    public AncSelectorViewModel Anc { get; }

    public IReadOnlyList<EqOption> EqOptions => DeviceOptions.Eq;

    public bool CanSetBassWave => Buds.IsConnected && Buds.BassWave is not null;

    /// <summary>The device's own lower bound. Never hardcoded.</summary>
    public double BassWaveMinimum => Buds.BassWave?.Minimum ?? 0;

    /// <summary>The device's own upper bound. Never hardcoded.</summary>
    public double BassWaveMaximum => Buds.BassWave?.Maximum ?? 0;

    /// <summary>
    /// Two-way for the slider. A drag is written once, after the thumb settles — not once per
    /// tick it passes on the way.
    /// </summary>
    public double BassWaveLevel
    {
        get => _pendingBassWave ?? Buds.BassWave?.Current ?? 0;
        set
        {
            var level = (int)Math.Round(value);
            if (!CanSetBassWave || Buds.BassWave is not { } current
                || level == (_pendingBassWave ?? current.Current))
            {
                return;
            }

            _pendingBassWave = level;
            OnPropertyChanged(nameof(BassWaveText));

            _bassWaveDebounce?.Cancel();
            _bassWaveDebounce?.Dispose();
            _bassWaveDebounce = new CancellationTokenSource();
            _ = CommitBassWaveAsync(level, _bassWaveDebounce.Token);
        }
    }

    public string BassWaveText
    {
        get
        {
            var level = (int)BassWaveLevel;
            return level > 0 ? $"+{level}" : level.ToString(CultureInfo.CurrentCulture);
        }
    }

    public string BassWaveNote => Buds.BassWaveSwitchOn == false
        ? "BassWave is switched off on the earbuds, so this level is stored but not heard until it is turned back on."
        : "Written to the earbuds when the slider settles, then read back.";

    [RelayCommand]
    private Task SetEqAsync(EqPreset preset) => Buds.SetEqPresetAsync(preset);

    private async Task CommitBassWaveAsync(int level, CancellationToken token)
    {
        try
        {
            await Task.Delay(BassWaveSettle, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Buds.SetBassWaveAsync(level);

        // A newer drag superseded this one while it was on the wire: leave that one pending.
        // Otherwise show what the device now reports, which is the truth even if it refused.
        if (_pendingBassWave == level)
        {
            _pendingBassWave = null;
            OnPropertyChanged(nameof(BassWaveLevel));
            OnPropertyChanged(nameof(BassWaveText));
        }
    }
}

/// <summary>Touch gestures, and the misc feature switches (still read-only).</summary>
public sealed partial class ControlsViewModel : ObservableObject
{
    public ControlsViewModel(BudsController buds)
    {
        Buds = buds;
        Gestures = DeviceOptions.EditableGestures
            .Select(gesture => new GestureRowViewModel(
                gesture.Label,
                new GestureSideViewModel(buds, BudSide.Left, gesture.Gesture),
                new GestureSideViewModel(buds, BudSide.Right, gesture.Gesture)))
            .ToArray();

        buds.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BudsController.TouchBindings) or nameof(BudsController.IsConnected))
            {
                foreach (var row in Gestures)
                {
                    row.Left.Refresh();
                    row.Right.Refresh();
                }

                OnPropertyChanged(nameof(OtherBindings));
            }
        };
    }

    public BudsController Buds { get; }

    public IReadOnlyList<GestureRowViewModel> Gestures { get; }

    /// <summary>Every binding the matrix does not offer, shown exactly as read.</summary>
    public IReadOnlyList<TouchBinding> OtherBindings => Buds.TouchBindings
        .Where(binding => binding.ContextType != DeviceOptions.PlaybackContext
                          || DeviceOptions.EditableGestures.All(g => g.Gesture != binding.Gesture))
        .ToArray();
}

/// <summary>One gesture, with a dropdown for each earbud.</summary>
public sealed class GestureRowViewModel(string label, GestureSideViewModel left, GestureSideViewModel right)
{
    public string Label { get; } = label;

    public GestureSideViewModel Left { get; } = left;

    public GestureSideViewModel Right { get; } = right;
}

/// <summary>One earbud's binding for one gesture: a dropdown that writes when changed.</summary>
public sealed partial class GestureSideViewModel(BudsController buds, BudSide side, byte gesture) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isWriting;

    /// <summary>The binding as last read from the earbuds, or null if there is none.</summary>
    private TouchBinding? Binding => buds.TouchBindings
        .Where(b => b.Side == side && b.ContextType == DeviceOptions.PlaybackContext && b.Gesture == gesture)
        .Select(b => (TouchBinding?)b)
        .FirstOrDefault();

    public bool CanEdit => buds.IsConnected && Binding is not null && !IsWriting;

    /// <summary>
    /// The known actions, plus the current one if it is not among them — so an unexpected
    /// value is shown as read instead of as a blank.
    /// </summary>
    public IReadOnlyList<TouchActionOption> Options =>
        Binding is { } binding && DeviceOptions.TouchActions.All(option => option.Action != binding.Action)
            ? [.. DeviceOptions.TouchActions, new(binding.Action, $"0x{binding.Action:X2} (as read)")]
            : DeviceOptions.TouchActions;

    public TouchActionOption? Selected
    {
        get => Binding is { } binding
            ? Options.FirstOrDefault(option => option.Action == binding.Action)
            : null;

        set
        {
            if (value is null || IsWriting || Binding is not { } binding || value.Action == binding.Action)
            {
                return;
            }

            _ = WriteAsync(binding, value.Action);
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(CanEdit));
    }

    private async Task WriteAsync(TouchBinding binding, byte action)
    {
        IsWriting = true;
        try
        {
            await buds.SetTouchActionAsync(binding, action);
        }
        finally
        {
            IsWriting = false;

            // Whatever the device now says wins, including when it refused the change.
            Refresh();
        }
    }
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
