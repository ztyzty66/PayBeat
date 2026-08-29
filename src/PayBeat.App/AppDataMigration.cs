namespace PayBeat.App.Services;

/// <summary>
/// Handles safe migration of user data from the legacy %APPDATA%\PayBeat directory to
/// %APPDATA%\今日薪动. If the new directory already has data, it takes priority. If only
/// the old directory exists, data is copied (not moved) as a backup. Migration failures
/// fall back to the old path.
/// </summary>
public static class AppDataMigration
{
    private static readonly string OldBasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PayBeat");
    private static readonly string NewBasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "今日薪动");

    /// <summary>
    /// Resolves the effective AppData directory. If the new path has data, use it.
    /// If only the old path has data, migrate to the new path. Returns the directory
    /// to use for all application data.
    /// </summary>
    public static string ResolveAndMigrate()
    {
        try
        {
            var newHasData = Directory.Exists(NewBasePath) && HasSettingsFile(NewBasePath);
            var oldHasData = Directory.Exists(OldBasePath) && HasSettingsFile(OldBasePath);

            if (newHasData)
            {
                // New directory already has data — use it
                AppLogger.Log($"AppData: using new path {NewBasePath}");
                return NewBasePath;
            }

            if (oldHasData && !newHasData)
            {
                // Old directory has data, new doesn't — migrate
                AppLogger.Log($"AppData: migrating from {OldBasePath} to {NewBasePath}");
                MigrateDirectory(OldBasePath, NewBasePath);
                return NewBasePath;
            }

            // Neither has data (fresh install) — use new path
            AppLogger.Log($"AppData: fresh install, using {NewBasePath}");
            return NewBasePath;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AppDataMigration.ResolveAndMigrate", ex);
            // Migration failed — fall back to old path if it exists, otherwise new
            return Directory.Exists(OldBasePath) ? OldBasePath : NewBasePath;
        }
    }

    private static bool HasSettingsFile(string basePath)
    {
        return File.Exists(Path.Combine(basePath, "settings.json"));
    }

    private static void MigrateDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        // Copy settings.json
        var settingsSrc = Path.Combine(source, "settings.json");
        var settingsDst = Path.Combine(destination, "settings.json");
        if (File.Exists(settingsSrc) && !File.Exists(settingsDst))
        {
            File.Copy(settingsSrc, settingsDst);
        }

        // Copy history directory
        var historySrc = Path.Combine(source, "history");
        var historyDst = Path.Combine(destination, "history");
        if (Directory.Exists(historySrc))
        {
            CopyDirectory(historySrc, historyDst);
        }

        // Copy logs directory
        var logsSrc = Path.Combine(source, "logs");
        var logsDst = Path.Combine(destination, "logs");
        if (Directory.Exists(logsSrc))
        {
            CopyDirectory(logsSrc, logsDst);
        }

        AppLogger.Log($"AppData: migration complete. Old directory preserved at {source}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(destFile))
            {
                File.Copy(file, destFile);
            }
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }
}
