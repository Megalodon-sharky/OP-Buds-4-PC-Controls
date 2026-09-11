using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace OpBuds.App.Services;

/// <summary>
/// A notification-area icon, driven straight through <c>Shell_NotifyIcon</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand rather than using WPF-UI's <c>NotifyIcon</c>, which would have been the
/// obvious choice. That control registers itself from <c>Loaded</c>, and this app frequently
/// has no loaded window at all — it starts in the tray. Registering it explicitly against a
/// parent handle was tried three ways (<c>ParentHandle</c> alone, a PNG icon instead of the
/// <c>.ico</c> WPF cannot decode, and <c>HookWindow</c> as well) and <c>Register()</c>
/// declined every time, leaving no icon and no error.
/// </para>
/// <para>
/// The shell API underneath is small, stable and does exactly one thing, so calling it
/// directly is less code than working around the wrapper — and it restores left-click, which
/// WPF-UI's code-first tray service does not expose at all.
/// </para>
/// <para>
/// The icon itself comes from the executable's own embedded resource via
/// <c>ExtractIconEx</c>. That sidesteps image decoding entirely: the shell wants an
/// <c>HICON</c>, the exe already contains one, and no bitmap ever has to be parsed.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    /// <summary>WM_APP + 1. Anything in the WM_APP range is ours to define.</summary>
    private const int TrayCallbackMessage = 0x0400 + 1;

    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint IconId = 1;

    private IntPtr _window;
    private IntPtr _icon;
    private HwndSource? _source;
    private HwndSourceHook? _hook;

    public bool IsRegistered { get; private set; }

    /// <summary>Shown on right-click. Optional.</summary>
    public ContextMenu? Menu { get; set; }

    public event EventHandler? LeftClick;

    /// <summary>
    /// Adds the icon. Returns false if the shell refused it.
    /// </summary>
    /// <remarks>
    /// The caller is expected to act on false rather than ignore it: an app that starts
    /// hidden and has no tray icon has no user-reachable surface at all.
    /// </remarks>
    public bool Register(IntPtr parentWindow, string tooltip)
    {
        _window = parentWindow;
        _source = HwndSource.FromHwnd(parentWindow);

        if (_source is null)
        {
            return false;
        }

        _hook = WndProc;
        _source.AddHook(_hook);

        _icon = LoadExecutableIcon();

        var data = Describe(NifMessage | NifIcon | NifTip, tooltip);
        IsRegistered = ShellNotifyIcon(NimAdd, ref data);
        return IsRegistered;
    }

    /// <summary>Updates the hover text. Silently does nothing when not registered.</summary>
    public void SetTooltip(string tooltip)
    {
        if (!IsRegistered)
        {
            return;
        }

        var data = Describe(NifTip, tooltip);
        ShellNotifyIcon(NimModify, ref data);
    }

    /// <summary>
    /// Draws the battery percentage into the icon itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The number is painted on the app's blue badge rather than as bare white glyphs. A
    /// tray icon has no control over the taskbar behind it, and plain white text vanishes
    /// on a light taskbar; a filled badge reads on either.
    /// </para>
    /// <para>
    /// Passing null restores the plain app icon — that is what "no reading" looks like,
    /// rather than a stale number that is quietly wrong.
    /// </para>
    /// </remarks>
    public void SetBattery(int? percent)
    {
        if (!IsRegistered)
        {
            return;
        }

        var replacement = percent is { } value ? RenderPercent(value) : LoadExecutableIcon();
        if (replacement == IntPtr.Zero)
        {
            return;
        }

        var previous = _icon;
        _icon = replacement;

        var data = Describe(NifIcon, string.Empty);
        ShellNotifyIcon(NimModify, ref data);

        // Only after the shell has taken the new one: destroying the icon it is still
        // drawing leaves an empty slot in the notification area.
        if (previous != IntPtr.Zero)
        {
            DestroyIcon(previous);
        }
    }

    private static IntPtr RenderPercent(int percent)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var canvas = Graphics.FromImage(bitmap);

        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        canvas.Clear(Color.Transparent);

        using (var badge = new SolidBrush(Color.FromArgb(255, 47, 111, 235)))
        using (var shape = RoundedSquare(32, 7))
        {
            canvas.FillPath(badge, shape);
        }

        var text = Math.Clamp(percent, 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Three digits need a smaller face or "100" runs off both edges.
        var size = text.Length >= 3 ? 14f : 21f;

        using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.White);
        using var layout = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        canvas.DrawString(text, font, ink, new RectangleF(0, 0, 32, 32), layout);

        return bitmap.GetHicon();
    }

    private static GraphicsPath RoundedSquare(int side, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(side - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(side - diameter, side - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, side - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    private NotifyIconData Describe(uint flags, string tooltip) => new()
    {
        CbSize = Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = IconId,
        Flags = flags,
        CallbackMessage = TrayCallbackMessage,
        Icon = _icon,

        // The shell truncates at 127 characters plus a terminator; a longer string makes
        // the whole call fail rather than the text shorten.
        Tip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private static IntPtr LoadExecutableIcon()
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return IntPtr.Zero;
        }

        var small = new IntPtr[1];
        return ExtractIconEx(executable, 0, null, small, 1) > 0 ? small[0] : IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != TrayCallbackMessage)
        {
            return IntPtr.Zero;
        }

        switch ((int)lParam)
        {
            case WmLeftButtonUp:
                LeftClick?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case WmRightButtonUp:
                ShowMenu();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        if (Menu is null)
        {
            return;
        }

        // Without this the menu does not dismiss when the user clicks elsewhere: a tray
        // menu belongs to a window that is not foreground, and Windows will not close a
        // popup owned by a background window.
        SetForegroundWindow(_window);

        Menu.Placement = PlacementMode.MousePoint;
        Menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (IsRegistered)
        {
            var data = Describe(0, string.Empty);
            ShellNotifyIcon(NimDelete, ref data);
            IsRegistered = false;
        }

        if (_hook is not null)
        {
            _source?.RemoveHook(_hook);
            _hook = null;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int CbSize;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
