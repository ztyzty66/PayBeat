namespace PayBeat.App.Services;

/// <summary>
/// Handles safe, resumable migration of user data from the legacy %APPDATA%\PayBeat directory to
/// %APPDATA%\今日薪动. Uses a migration marker (.migration-v1-complete) so interrupted migrations
/// are resumed on the next launch. Old directory is preserved as a safety backup.
/// </summary>
public static class AppDataMigration
{
    private static readonly string OldBasePath = AppPaths.LegacyRoot;
    private static readonly string NewBasePath = AppPaths.DataRoot;
    private static readonly string MarkerPath = AppPaths.MigrationMarker;

    /// <summary>
    /// Resolves the effective AppData directory. Idempotent and resumable:
    /// if the migration marker exists, migration is considered complete.
    /// If settings exist at the new path but the marker is missing, incomplete
    /// migration data is backfilled from the old path.
    /// </summary>
    public static string ResolveAndMigrate()
    {
        try
        {
            Directory.CreateDirectory(NewBasePath);

            // Marker present: migration was completed in a prior run.
            if (File.Exists(MarkerPath))
            {
                return NewBasePath;
            }

            var newHasSettings = File.Exists(Path.Combine(NewBasePath, "settings.json"));
            var oldHasSettings = File.Exists(Path.Combine(OldBasePath, "settings.json"));

            if (oldHasSettings)
            {
                // Migrate (or resume) from old path.
                AppLogger.Log($"AppData: migrating from {OldBasePath} to {NewBasePath}");
                MigrateDirectory(OldBasePath, NewBasePath);
            }
            else if (!newHasSettings)
            {
                // Fresh install.
                AppLogger.Log($"AppData: fresh install, using {NewBasePath}");
            }

            // Mark migration as complete (even for fresh install — prevents re-check).
            try { File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("O")); } catch { }

            return NewBasePath;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("AppDataMigration.ResolveAndMigrate", ex);
            return Directory.Exists(OldBasePath) ? OldBasePath : NewBasePath;
        }
    }

    private static void MigrateDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        // Copy settings.json — NEVER overwrite if destination already has one (new-data-wins).
        var settingsSrc = Path.Combine(source, "settings.json");
        var settingsDst = Path.Combine(destination, "settings.json");
        if (File.Exists(settingsSrc) && !File.Exists(settingsDst))
        {
            File.Copy(settingsSrc, settingsDst);
        }

        // Copy history directory (skip files that already exist at destination).
        var historySrc = Path.Combine(source, "history");
        var historyDst = Path.Combine(destination, "history");
        if (Directory.Exists(historySrc))
        {
            CopyDirectory(historySrc, historyDst);
        }

        // Copy logs directory.
        var logsSrc = Path.Combine(source, "logs");
        var logsDst = Path.Combine(destination, "logs");
        if (Directory.Exists(logsSrc))
        {
            CopyDirectory(logsSrc, logsDst);
        }

        AppLogger.Log($"AppData: migration pass complete. Old directory preserved at {source}");
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
