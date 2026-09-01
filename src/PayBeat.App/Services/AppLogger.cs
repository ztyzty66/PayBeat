namespace PayBeat.App.Services;

/// <summary>
/// Lightweight file logger for best-effort diagnostics. Writes to
/// <c>%APPDATA%\今日薪动\logs\</c>. All methods swallow failures — logging must
/// never crash the application. Does not record sensitive content.
/// </summary>
public static class AppLogger
{
    private static readonly object Lock = new();
    private static string? _logDirectory;

    /// <summary>
    /// Initializes the log directory. Called once at startup.
    /// </summary>
    public static void Initialize(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? AppPaths.LogsDirectory;
        try { Directory.CreateDirectory(_logDirectory); } catch { }
    }

    /// <summary>
    /// Appends a timestamped message to the log file. Thread-safe. Never throws.
    /// </summary>
    public static void Log(string message)
    {
        if (_logDirectory is null) Initialize();

        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            var path = Path.Combine(_logDirectory!, $"app-{DateTime.Now:yyyy-MM-dd}.log");
            lock (Lock)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    /// <summary>
    /// Logs an exception with context. Never throws.
    /// </summary>
    public static void LogError(string context, Exception ex)
    {
        Log($"ERROR [{context}] {ex.GetType().Name}: {ex.Message}");
    }
}
