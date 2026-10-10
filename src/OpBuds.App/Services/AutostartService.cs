using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace OpBuds.App.Services;

/// <summary>
/// Starts the app at sign-in, through a per-user Task Scheduler logon task.
/// </summary>
/// <remarks>
/// <para>
/// This used the registry <c>Run</c> key first, and on the machine it was built on Explorer
/// silently skipped that entry at every sign-in. The value was present and correct, not
/// disabled in Task Manager, not blocked by any policy — and never launched. The shell's own
/// operational log shows it starting every other enabled entry in the same key and not this
/// one, and Windows' Startup apps page leaves it out too. Nothing visible from outside
/// Explorer explained it.
/// </para>
/// <para>
/// Task Scheduler is a separate service with its own logon trigger, so it does not inherit
/// whatever Explorer decided. A standard user can register a logon task for themselves, and
/// the task can be run on demand — which, unlike the Run key, makes it testable without
/// signing out.
/// </para>
/// <para>
/// <see cref="IsEnabled"/> asks Task Scheduler every time rather than trusting a stored
/// boolean, so the toggle stays honest if the task is deleted or disabled by hand.
/// </para>
/// </remarks>
public sealed class AutostartService
{
    private const string TaskName = "OpBudsControls";

    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "OpBudsControls";

    /// <summary>Passed to the autostarted instance so it comes up in the tray, not on screen.</summary>
    public const string MinimizedArgument = "--minimized";

    /// <summary>
    /// Where Windows should launch from.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.ProcessPath"/> is the apphost <c>.exe</c>, which is what the task
    /// must run. The assembly location would be the managed <c>.dll</c> and would not launch.
    /// </remarks>
    public static string? ExecutablePath => Environment.ProcessPath;

    /// <summary>True when the logon task exists, is enabled, and launches this executable.</summary>
    public bool IsEnabled
    {
        get
        {
            if (ExecutablePath is not { } executable)
            {
                return false;
            }

            try
            {
                var schedulerType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
                dynamic scheduler = Activator.CreateInstance(schedulerType)!;
                scheduler.Connect();

                dynamic task = scheduler.GetFolder(@"\").GetTask(TaskName);
                string definition = task.Xml;
                bool enabled = task.Enabled;

                return enabled &&
                       definition.Contains(SecurityElement.Escape(executable), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is COMException or TargetInvocationException or UnauthorizedAccessException)
            {
                // No such task, or Task Scheduler is unavailable: either way, not enabled.
                return false;
            }
        }
    }

    /// <summary>Returns what Task Scheduler actually says afterwards, not what was asked for.</summary>
    public bool Set(bool enabled)
    {
        if (enabled)
        {
            if (ExecutablePath is not { } executable)
            {
                return false;
            }

            Register(executable);
        }
        else
        {
            Schtasks("/Delete", "/TN", TaskName, "/F");
        }

        var actual = IsEnabled;

        // The old Run entry goes once the task has replaced it, or when autostart is being
        // switched off anyway. Never before: until then it is the only record of an "on".
        if (actual == enabled)
        {
            RemoveLegacyRunEntry();
        }

        return actual;
    }

    /// <summary>
    /// Moves an install that used the Run key onto the logon task. Safe to call at every start.
    /// </summary>
    /// <remarks>
    /// Acts only while the old Run entry exists, because that entry is the user's earlier
    /// "on" — it is not this method's place to switch autostart on for anyone else.
    /// </remarks>
    public void MigrateFromRunKey()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey))
        {
            if (key?.GetValue(LegacyValueName) is null)
            {
                return;
            }
        }

        Set(true);
    }

    private static void Register(string executable)
    {
        // schtasks wants the file's real encoding to match the declaration inside it.
        var file = Path.Combine(Path.GetTempPath(), $"opbuds-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, Definition(executable), Encoding.Unicode);
            Schtasks("/Create", "/TN", TaskName, "/XML", file, "/F");
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A stray temp file is not worth failing the toggle over.
            }
        }
    }

    /// <remarks>
    /// The ten-second delay gives Explorer time to build the taskbar before the tray icon is
    /// added; the tray also retries and listens for <c>TaskbarCreated</c>, so the delay is a
    /// courtesy rather than a requirement. There is no time limit, because Task Scheduler's
    /// default stops a task after three days. Priority 5 is normal — the default of 7 would
    /// run the app below normal.
    /// </remarks>
    private static string Definition(string executable)
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var command = SecurityElement.Escape(executable);
        var folder = SecurityElement.Escape(Path.GetDirectoryName(executable) ?? string.Empty);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts OpBuds in the notification area when you sign in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{MinimizedArgument}</Arguments>
                  <WorkingDirectory>{folder}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static void RemoveLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            // Policy can lock the Run key down. The task works regardless.
        }
    }

    private static void Schtasks(params string[] arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            process?.WaitForExit(15_000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // schtasks missing or refused: IsEnabled reports the outcome either way.
        }
    }
}
