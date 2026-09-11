using Microsoft.Win32;

namespace OpBuds.App.Services;

/// <summary>
/// The <c>HKCU ... \Run</c> entry that starts the app with Windows.
/// </summary>
/// <remarks>
/// <see cref="IsEnabled"/> reads the registry every time rather than trusting a stored
/// boolean. Windows lets the user disable a startup entry from Task Manager without telling
/// the app, and a toggle that claims "on" while nothing runs at boot is worse than no toggle.
/// </remarks>
public sealed class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpBudsControls";

    /// <summary>Passed to the autostarted instance so it comes up in the tray, not on screen.</summary>
    public const string MinimizedArgument = "--minimized";

    /// <summary>True when a Run entry exists and points at this executable.</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string command)
            {
                return false;
            }

            var executable = ExecutablePath;
            return executable is not null &&
                   command.Contains(executable, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Where Windows should launch from.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.ProcessPath"/> is the apphost <c>.exe</c>, which is what must
    /// go in the registry. The assembly location would be the managed <c>.dll</c> and would
    /// not launch.
    /// </remarks>
    public static string? ExecutablePath => Environment.ProcessPath;

    /// <summary>Returns what the registry actually says afterwards, not what was asked for.</summary>
    public bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return IsEnabled;
            }

            if (enabled)
            {
                if (ExecutablePath is not { } executable)
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{executable}\" {MinimizedArgument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Policy can lock the Run key down. Report reality rather than pretending.
        }

        return IsEnabled;
    }
}
