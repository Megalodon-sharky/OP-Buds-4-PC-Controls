using System.Text.Json.Serialization;

namespace OpBuds.App.Services;

/// <summary>
/// Everything the app remembers between runs.
/// </summary>
/// <remarks>
/// A record with <c>init</c> setters rather than a mutable bag: settings are replaced
/// wholesale by <see cref="SettingsStore.Update"/>, which makes "changed" a single
/// comparison and makes a half-applied settings object impossible.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>The paired earbuds. Defaults to the unit this project was developed against.</summary>
    public string DeviceMac { get; init; } = "12:34:56:78:9A:BC";

    /// <summary>
    /// A cache of the registry Run entry, never the authority.
    /// </summary>
    /// <remarks>
    /// The user can delete the Run entry from Task Manager's Startup tab at any time, and
    /// this file would never hear about it. <see cref="AutostartService.IsEnabled"/> reads
    /// the registry instead; this field exists only so the settings file is readable.
    /// </remarks>
    public bool StartWithWindows { get; init; }

    /// <summary>
    /// Whether a <i>manual</i> launch goes straight to the tray. Off by default.
    /// </summary>
    /// <remarks>
    /// Autostart does not depend on this: the registry Run entry passes
    /// <see cref="AutostartService.MinimizedArgument"/> explicitly. Defaulting this to true
    /// as well would mean double-clicking the app appears to do nothing at all, with the
    /// only feedback a new icon hidden in the notification-area overflow.
    /// </remarks>
    public bool StartMinimized { get; init; }

    public bool CloseToTray { get; init; } = true;

    /// <summary>Whether the "it is still running in the tray" note has been shown once.</summary>
    public bool CloseToTrayExplained { get; init; }

    /// <summary>Seconds between battery polls while a window is on screen.</summary>
    public int PollSecondsVisible { get; init; } = 5;

    /// <summary>Writes a JSONL capture of every frame to the logs directory.</summary>
    public bool ProtocolLogging { get; init; }

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; init; } = "system";
}

/// <summary>
/// Source-generated serialisation, so settings survive trimming and single-file publish.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsContext : JsonSerializerContext;
