namespace PayBeat.App.Services;

/// <summary>
/// Single source of truth for all application data paths. All runtime services
/// must resolve their directories from this class — never hardcode paths.
/// The legacy path is only used as a migration source.
/// </summary>
public static class AppPaths
{
    private static readonly string AppDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "今日薪动");

    /// <summary>Legacy path used only as a migration source.</summary>
    public static readonly string LegacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PayBeat");

    /// <summary>Root data directory: %APPDATA%\今日薪动</summary>
    public static string DataRoot => AppDataRoot;

    /// <summary>Settings directory: %APPDATA%\今日薪动\settings.json</summary>
    public static string SettingsFile => Path.Combine(AppDataRoot, "settings.json");

    /// <summary>History directory: %APPDATA%\今日薪动\history</summary>
    public static string HistoryDirectory => Path.Combine(AppDataRoot, "history");

    /// <summary>Logs directory: %APPDATA%\今日薪动\logs</summary>
    public static string LogsDirectory => Path.Combine(AppDataRoot, "logs");

    /// <summary>Migration completion marker file.</summary>
    public static string MigrationMarker => Path.Combine(AppDataRoot, ".migration-v1-complete");
}
