using PayBeat.App.Domain;
using PayBeat.App.Models;

namespace PayBeat.App.Services;

/// <summary>
/// Loads and saves <see cref="SalarySettings"/> as JSON. Returns default settings when the
/// file is absent or unreadable. Legacy (v1/v2) flat settings are migrated into the versioned
/// profile model (v3) on load. Supports injectable paths for testability and atomic writes
/// to prevent data corruption on failure.
/// </summary>
public class SettingsService
{
    private readonly string _filePath;
    private readonly string _directoryPath;

    /// <summary>Creates a SettingsService with the default AppData path.</summary>
    public SettingsService() : this(DefaultDirectoryPath()) { }

    /// <summary>Creates a SettingsService with an explicit directory path (for testing).</summary>
    public SettingsService(string directoryPath)
    {
        _directoryPath = directoryPath;
        _filePath = Path.Combine(directoryPath, "settings.json");
    }

    private static string DefaultDirectoryPath() => AppPaths.DataRoot;

    /// <summary>Exposes the settings directory for sibling stores (history snapshots).</summary>
    public string SettingsDirectory => _directoryPath;

    /// <summary>The full path to the settings file.</summary>
    public string FilePath => _filePath;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new TimeOnlyConverter(), new DisplayModeConverter() }
    };

    /// <summary>
    /// Reads settings from disk, applying the v1/v2→v3 migration when needed. Returns a default
    /// <see cref="SalarySettings"/> if the file does not exist or cannot be deserialized.
    /// </summary>
    public SalarySettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new SalarySettings();
        }
        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<SalarySettings>(json, Options) ?? new SalarySettings();
            return Migrate(settings);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Settings load failed: {ex.Message}");
            BackupCorruptFile();
            return new SalarySettings();
        }
    }

    /// <summary>
    /// Atomically persists settings to disk: serialize to memory → write temp file → flush → rename.
    /// If any step fails, the original file remains intact.
    /// </summary>
    public void Save(SalarySettings settings)
    {
        Directory.CreateDirectory(_directoryPath);
        var json = JsonSerializer.Serialize(settings with { ConfigVersion = 3 }, Options);
        var tempPath = _filePath + ".tmp";
        var backupPath = _filePath + ".bak";

        try
        {
            File.WriteAllText(tempPath, json);
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                fs.Flush(true);
            }

            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, backupPath, overwrite: true);
                File.Replace(tempPath, _filePath, null);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Settings save failed: {ex.Message}");
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Upgrades legacy settings to v3. v1: flat → versioned profiles. v2: already versioned, bump version.
    /// Idempotent: already-migrated settings pass through unchanged.
    /// </summary>
    public static SalarySettings Migrate(SalarySettings s)
    {
        SalarySettings result = s;

        if (s.ConfigVersion == 1)
        {
            // v1 → v3: full transformation
            var salaryProfile = new SalaryProfile
            {
                Mode = SalaryMode.Daily,
                DailyAmount = s.DailySalary,
                MonthlyAmount = 0m,
                EffectiveFrom = new DateOnly(2000, 1, 1),
            };

            var schedule = new WorkScheduleProfile
            {
                Id = PayConfiguration.DefaultScheduleId,
                Name = s.LegacyScheduleName,
                WorkStart = s.WorkStart,
                WorkEnd = s.WorkEnd,
                LunchBreakEnabled = s.LunchBreakEnabled,
                LunchBreakStart = s.LunchBreakStart,
                LunchBreakEnd = s.LunchBreakEnd,
                EffectiveFrom = new DateOnly(2000, 1, 1),
            };

            var policy = s.WorkOnWeekends
                ? new WorkWeekPolicy
                {
                    Type = WorkWeekType.Custom,
                    WorkDays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
                    EffectiveFrom = new DateOnly(2000, 1, 1),
                }
                : WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1));

            result = s with
            {
                ConfigVersion = 3,
                SalaryProfiles = [salaryProfile],
                ScheduleProfiles = [schedule],
                WeekPolicies = [policy],
                SetupCompleted = true,
                LegacyScheduleName = s.LegacyScheduleName,
            };
        }
        else if (s.ConfigVersion == 2)
        {
            result = s with { ConfigVersion = 3 };
        }

        // Always normalize: deduplicate same-date profiles (last-write-wins) so
        // dirty data from any schema version enters runtime in a clean state.
        return Normalize(result);
    }

    /// <summary>
    /// Deduplicates versioned profile collections so that at most one entry per
    /// EffectiveFrom date remains (last-write-wins, deterministic).
    /// </summary>
    private static SalarySettings Normalize(SalarySettings s) => s with
    {
        SalaryProfiles = ProfileVersioning.Normalize(s.SalaryProfiles, p => p.EffectiveFrom),
        ScheduleProfiles = ProfileVersioning.Normalize(s.ScheduleProfiles, p => p.EffectiveFrom),
        WeekPolicies = ProfileVersioning.Normalize(s.WeekPolicies, p => p.EffectiveFrom),
    };

    private void BackupCorruptFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, _filePath + ".bak", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Backup corrupt settings failed: {ex.Message}");
        }
    }

    private sealed class DisplayModeConverter : JsonConverter<DisplayMode>
    {
        public override DisplayMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse<DisplayMode>(reader.GetString(), ignoreCase: true, out var mode))
            {
                return mode;
            }
            return DisplayMode.None;
        }

        public override void Write(Utf8JsonWriter writer, DisplayMode value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    private sealed class TimeOnlyConverter : JsonConverter<TimeOnly>
    {
        public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => TimeOnly.ParseExact(reader.GetString()!, "HH:mm");

        public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString("HH:mm"));
    }
}
