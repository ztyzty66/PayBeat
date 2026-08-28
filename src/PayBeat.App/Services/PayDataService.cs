using PayBeat.App.Domain;
using PayBeat.App.Models;

namespace PayBeat.App.Services;

/// <summary>
/// Loads the built-in official holiday dataset (embedded resource, offline). Loading is
/// fault-tolerant: a missing/malformed dataset yields an empty calendar and computation simply
/// falls back to the weekly policy — statutory-holiday data must never break pay calculation.
/// </summary>
public static class HolidayService
{
    private const string ResourcePath = "PayBeat.App.Resources.holidays.json";

    private static readonly Lazy<HolidayCalendar> Calendar = new(LoadEmbedded);

    /// <summary>The built-in calendar (shared immutable instance).</summary>
    public static HolidayCalendar BuiltIn => Calendar.Value;

    private static HolidayCalendar LoadEmbedded()
    {
        try
        {
            var assembly = typeof(HolidayService).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourcePath);
            if (stream is null)
            {
                return new HolidayCalendar([]);
            }
            using var reader = new StreamReader(stream);
            return HolidayCalendar.FromJson(reader.ReadToEnd());
        }
        catch
        {
            return new HolidayCalendar([]);
        }
    }
}

/// <summary>
/// Aggregates settings, history, and holiday data into a <see cref="PayConfiguration"/> and
/// handles history snapshot bookkeeping (recording past days, finalizing months). This is the
/// single façade view-models use to answer pay questions.
/// </summary>
public class PayDataService
{
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;

    public PayDataService(SettingsService settingsService, HistoryService historyService)
    {
        _settingsService = settingsService;
        _historyService = historyService;
    }

    /// <summary>Builds the effective configuration from persisted settings + built-in holidays.</summary>
    public PayConfiguration BuildConfiguration()
    {
        var s = _settingsService.Load();
        return BuildConfiguration(s);
    }

    /// <summary>Builds a configuration from explicit settings (also used by tests via subclassing).</summary>
    public PayConfiguration BuildConfiguration(SalarySettings s) => new()
    {
        SalaryProfiles = s.SalaryProfiles,
        ScheduleProfiles = s.ScheduleProfiles,
        WeekPolicies = s.WeekPolicies,
        Overrides = ParseOverrides(s.Overrides),
        Holidays = HolidayService.BuiltIn,
        LegacyScheduleName = s.LegacyScheduleName,
    };

    /// <summary>Converts the JSON-friendly "yyyy-MM-dd" keys into typed DateOnly keys, skipping bad rows.</summary>
    private static IReadOnlyDictionary<DateOnly, CalendarOverride> ParseOverrides(Dictionary<string, CalendarOverride> raw)
    {
        var result = new Dictionary<DateOnly, CalendarOverride>();
        foreach (var (key, value) in raw)
        {
            if (DateOnly.TryParseExact(key, "yyyy-MM-dd", out var date))
            {
                result[date] = value with { Date = date };
            }
        }
        return result;
    }

    public HistoryService History => _historyService;

    /// <summary>
    /// Persists a day's snapshot (configuration + result) into its month file. Called when a day
    /// completes — the next day rolls over or the app exits after work end.
    /// </summary>
    public void SnapshotDay(DayComputation day, PayConfiguration config, int plannedWorkdays)
    {
        _historyService.RecordDay(day.Date, new DayHistoryRecord
        {
            Date = day.Date,
            Status = day.Status,
            DailyRate = day.DailyRate,
            TargetEarned = day.TargetEarned,
            FinalEarned = day.FinalEarned,
            LeaveSeconds = day.LeaveSeconds,
            SalaryProfileSnapshot = config.ResolveSalaryProfile(day.Date),
            ScheduleSnapshot = day.Schedule,
            WeekPolicySnapshot = config.ResolveWeekPolicy(day.Date),
            PlannedWorkdaysSnapshot = plannedWorkdays,
        });
    }
}
