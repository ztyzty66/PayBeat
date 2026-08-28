using PayBeat.App.Domain;
using PayBeat.App.Models;

namespace PayBeat.App.Services;

/// <summary>
/// Loads and saves <see cref="SalarySettings"/> as JSON at <c>%APPDATA%\PayBeat\settings.json</c>.
/// Returns default settings when the file is absent or unreadable. Legacy (v1) flat settings —
/// daily salary, work times, lunch break, weekend flag — are migrated once into the versioned
/// profile model (v2) on load; the legacy fields remain in the JSON for backward compatibility.
/// </summary>
public class SettingsService
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "PayBeat", "settings.json");

    private static readonly string DirectoryPath =
        Path.GetDirectoryName(FilePath)!;

    /// <summary>Exposes the settings directory for sibling stores (history snapshots).</summary>
    public static string SettingsDirectory => DirectoryPath;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new TimeOnlyConverter(), new DisplayModeConverter() }
    };

    /// <summary>
    /// Reads settings from disk, applying the legacy→v2 migration when needed. Returns a default
    /// <see cref="SalarySettings"/> if the file does not exist or cannot be deserialized.
    /// </summary>
    public SalarySettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new SalarySettings();
        }
        try
        {
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<SalarySettings>(json, Options) ?? new SalarySettings();
            return Migrate(settings);
        }
        catch
        {
            BackupCorruptFile();
            return new SalarySettings();
        }
    }

    /// <summary>
    /// Serializes <paramref name="settings"/> to disk, creating the directory if needed.
    /// </summary>
    /// <param name="settings">Settings to persist.</param>
    public void Save(SalarySettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings with { ConfigVersion = 2 }, Options));
    }

    /// <summary>
    /// Upgrades legacy flat settings (ConfigVersion &lt; 2) to the versioned model:
    /// daily salary → a Daily-mode salary profile; work window + lunch → a schedule profile;
    /// weekend flag → a week policy. Idempotent: already-migrated settings pass through unchanged.
    /// </summary>
    public static SalarySettings Migrate(SalarySettings s)
    {
        if (s.ConfigVersion >= 2)
        {
            return s;
        }

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

        return s with
        {
            ConfigVersion = 2,
            SalaryProfiles = [salaryProfile],
            ScheduleProfiles = [schedule],
            WeekPolicies = [policy],
            SetupCompleted = true,
            LegacyScheduleName = s.LegacyScheduleName,
        };
    }

    /// <summary>
    /// Preserves an unreadable settings file as <c>settings.json.bak</c> (overwriting any prior
    /// backup) so a load failure doesn't silently destroy the user's saved position/preferences.
    /// Swallows failures — this is a best-effort safety net, not a critical path.
    /// </summary>
    private static void BackupCorruptFile()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".bak", overwrite: true);
        }
        catch
        {
            // Best-effort; if we can't back it up, still fall back to defaults.
        }
    }

    /// <summary>
    /// Serializes <see cref="DisplayMode"/> as its name (e.g. <c>"Flex"</c>) instead of an ordinal
    /// number, so persisted values remain stable across future enum reordering. Any value that
    /// can't be parsed as a known name — including old files that stored the ordinal number —
    /// falls back to <see cref="DisplayMode.None"/> instead of failing the whole settings load.
    /// </summary>
    private sealed class DisplayModeConverter : JsonConverter<DisplayMode>
    {
        /// <inheritdoc/>
        public override DisplayMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse<DisplayMode>(reader.GetString(), ignoreCase: true, out var mode))
            {
                return mode;
            }

            return DisplayMode.None;
        }

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, DisplayMode value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    /// <summary>
    /// Serializes <see cref="TimeOnly"/> as an <c>HH:mm</c> string so the JSON file is human-readable.
    /// </summary>
    private sealed class TimeOnlyConverter : JsonConverter<TimeOnly>
    {
        /// <inheritdoc/>
        public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => TimeOnly.ParseExact(reader.GetString()!, "HH:mm");

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString("HH:mm"));
    }
}
