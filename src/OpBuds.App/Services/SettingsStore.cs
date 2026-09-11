using System.IO;
using System.Text.Json;

namespace OpBuds.App.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> without ever leaving a torn file behind.
/// </summary>
/// <remarks>
/// Settings are saved on tray-exit, which is exactly when the process is most likely to be
/// killed mid-write. A partially written JSON file would then fail to parse on next launch
/// and silently reset every preference — so the write goes to a temp file first and is moved
/// into place, and a parse failure falls back to defaults rather than throwing at startup.
/// </remarks>
public sealed class SettingsStore
{
    public const string FolderName = "OpBudsControls";

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(DefaultDirectory, "settings.json");
        Current = Load();
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    public static string LogDirectory => Path.Combine(DefaultDirectory, "logs");

    /// <summary>
    /// Where the settings live.
    /// </summary>
    /// <remarks>
    /// Named <c>FilePath</c> rather than <c>Path</c> on purpose: a member called
    /// <c>Path</c> shadows <see cref="System.IO.Path"/> for every line inside this class,
    /// and the resulting errors point at the call sites rather than at the cause.
    /// </remarks>
    public string FilePath => _path;

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    /// <summary>Applies a change and persists it. No-op when nothing actually differs.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings updated;

        lock (_gate)
        {
            updated = change(Current);
            if (updated == Current)
            {
                return;
            }

            Current = updated;
            Save(updated);
        }

        Changed?.Invoke(this, updated);
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize(json, AppSettingsContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt settings file must not stop the app from starting.
            return new AppSettings();
        }
    }

    private void Save(AppSettings settings)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, AppSettingsContext.Default.AppSettings));

            // Move-with-overwrite is the closest thing Win32 gives to an atomic replace of a
            // file that may not exist yet. A reader sees either the old file or the new one.
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a preference is annoying; crashing on exit is worse.
        }
    }
}
