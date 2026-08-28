using PayBeat.App.Domain;

namespace PayBeat.App.Services;

/// <summary>Immutable per-day record stored in the month history file.</summary>
public sealed record DayHistoryRecord
{
    public DateOnly Date { get; init; }

    public DayStatus Status { get; init; }

    /// <summary>Standard daily rate snapshot as computed that day (Decimal).</summary>
    public decimal DailyRate { get; init; }

    /// <summary>Day target snapshot (daily rate minus leave deduction; PTO pays in full).</summary>
    public decimal TargetEarned { get; init; }

    /// <summary>Final earned snapshot for the day.</summary>
    public decimal FinalEarned { get; init; }

    public double LeaveSeconds { get; init; }

    /// <summary>Salary configuration snapshot actually used for this day.</summary>
    public SalaryProfile? SalaryProfileSnapshot { get; init; }

    /// <summary>Schedule snapshot actually used for this day.</summary>
    public WorkScheduleProfile? ScheduleSnapshot { get; init; }

    /// <summary>Week policy snapshot actually used for this day.</summary>
    public WorkWeekPolicy? WeekPolicySnapshot { get; init; }

    /// <summary>Planned paid workdays of the month as of this day's computation (denominator snapshot).</summary>
    public int PlannedWorkdaysSnapshot { get; init; }
}

/// <summary>One month's history file content at <c>history/YYYY-MM.json</c>.</summary>
public sealed record MonthHistory
{
    public string Month { get; init; } = "";

    /// <summary>True once the month has rolled over and the file is treated as immutable.</summary>
    public bool Finalized { get; init; }

    /// <summary>Month-level aggregates captured at finalization.</summary>
    public decimal StandardMonthlySnapshot { get; init; }

    public decimal MonthTargetSnapshot { get; init; }

    public decimal MonthEarnedSnapshot { get; init; }

    public int PlannedWorkdays { get; init; }

    public int PtoDays { get; init; }

    public decimal LeaveHours { get; init; }

    public string WorkWeekTypeSnapshot { get; init; } = "";

    public Dictionary<string, DayHistoryRecord> Days { get; init; } = [];
}

/// <summary>
/// Persists per-day and per-month history snapshots under <c>%APPDATA%\PayBeat\history\</c>.
/// Snapshots freeze the configuration and results that were in effect on each day so later
/// settings edits can never rewrite the past (the "history is immutable" rule). Reads are
/// best-effort: a missing or corrupt file simply means "no data for that month".
/// </summary>
public class HistoryService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string FilePathFor(DateOnly month) => Path.Combine(
        SettingsService.SettingsDirectory, "history", $"{month.Year:D4}-{month.Month:D2}.json");

    /// <summary>Loads a month history, or <see langword="null"/> when absent/corrupt.</summary>
    public MonthHistory? Load(DateOnly month)
    {
        try
        {
            var path = FilePathFor(month);
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<MonthHistory>(File.ReadAllText(path), Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Merges a day record into the month file (creating it when needed) and saves.</summary>
    public void RecordDay(DateOnly month, DayHistoryRecord record)
    {
        try
        {
            var history = Load(month) ?? new MonthHistory
            {
                Month = $"{month.Year:D4}-{month.Month:D2}",
            };
            var days = new Dictionary<string, DayHistoryRecord>(history.Days)
            {
                [record.Date.ToString("yyyy-MM-dd")] = record,
            };
            Save(history with { Days = days });
        }
        catch
        {
            // History persistence must never crash the live widget.
        }
    }

    /// <summary>Writes month-level aggregates and marks the month finalized.</summary>
    public void FinalizeMonth(DateOnly month, MonthHistory finalized)
    {
        try
        {
            Save(finalized with { Finalized = true, Month = $"{month.Year:D4}-{month.Month:D2}" });
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>Replaces the whole month file (used when the user explicitly edits history).</summary>
    public void Save(MonthHistory history)
    {
        var path = FilePathFor(DateOnly.Parse(history.Month + "-01"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(history, Options));
    }

    /// <summary>Lists months that have history files, newest first.</summary>
    public IReadOnlyList<DateOnly> ListMonths()
    {
        try
        {
            var dir = Path.Combine(SettingsService.SettingsDirectory, "history");
            if (!Directory.Exists(dir))
            {
                return [];
            }
            return Directory.GetFiles(dir, "*.json")
                .Select(Path.GetFileName)
                .Select(name => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(name), "yyyy-MM", out var m) ? m : default)
                .Where(m => m != default)
                .OrderByDescending(m => m)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
