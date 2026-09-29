using System.Text.Json;

namespace SHARD.Settings;

/// <summary>
/// Persisted, app-level (not per-project) user preferences — a plain JSON file under the
/// user's application-data folder, since SHARD has no other settings-persistence mechanism.
/// </summary>
public sealed class AppSettings
{
    private const int DefaultLargeDatabaseThresholdMb = 1024;

    /// <summary>
    /// Opening a SQLite file at or above this size prompts the user to build a project directly
    /// on disk (see <see cref="Views.CreateProjectWindow"/>) instead of the temp file
    /// <c>ShadowProject.CreateTemporary</c> would otherwise use.
    /// </summary>
    public int LargeDatabaseThresholdMb { get; set; } = DefaultLargeDatabaseThresholdMb;

    public long LargeDatabaseThresholdBytes => (long)LargeDatabaseThresholdMb * 1024 * 1024;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SHARD", "settings.json");

    public static AppSettings Current { get; private set; } = Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null && loaded.LargeDatabaseThresholdMb > 0)
                    return loaded;
            }
        }
        catch { /* fall back to defaults on any read/parse error */ }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        Current = this;
    }
}
