using PayBeat.App.Domain;

namespace PayBeat.App.Services;

/// <summary>Immutable per-day record stored in the month history file.</summary>
public sealed record DayHistoryRecord
{
    public DateOnly Date { get; init; }
    public DayStatus Status { get; init; }
    public decimal DailyRate { get; init; }
    public decimal TargetEarned { get; init; }
    public decimal FinalEarned { get; init; }
    public double LeaveSeconds { get; init; }
    public SalaryProfile? SalaryProfileSnapshot { get; init; }
    public WorkScheduleProfile? ScheduleSnapshot { get; init; }
    public WorkWeekPolicy? WeekPolicySnapshot { get; init; }
    public int PlannedWorkdaysSnapshot { get; init; }
}

/// <summary>One month's history file content.</summary>
public sealed record MonthHistory
{
    public string Month { get; init; } = "";
    public bool Finalized { get; init; }
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
/// Persists per-day and per-month history snapshots. Supports injectable paths for testing.
/// Uses atomic writes and logs failures instead of silently swallowing them.
/// </summary>
public class HistoryService
{
    private readonly string _historyDirectory;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Creates a HistoryService using the default AppData path.</summary>
    public HistoryService() : this(Path.Combine(
        Path.GetDirectoryName(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PayBeat", "settings.json"))!,
        "history")) { }

    /// <summary>Creates a HistoryService with an explicit directory path (for testing).</summary>
    public HistoryService(string historyDirectory)
    {
        _historyDirectory = historyDirectory;
    }

    private string FilePathFor(DateOnly month) => Path.Combine(
        _historyDirectory, $"{month.Year:D4}-{month.Month:D2}.json");

    /// <summary>Loads a month history, or null when absent/corrupt.</summary>
    public MonthHistory? Load(DateOnly month)
    {
        try
        {
            var path = FilePathFor(month);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<MonthHistory>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"HistoryService.Load({month})", ex);
            return null;
        }
    }

    /// <summary>Merges a day record into the month file atomically.</summary>
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
        catch (Exception ex)
        {
            AppLogger.LogError($"HistoryService.RecordDay({month}, {record.Date})", ex);
        }
    }

    /// <summary>Writes month-level aggregates and marks the month finalized.</summary>
    public void FinalizeMonth(DateOnly month, MonthHistory finalized)
    {
        try
        {
            Save(finalized with { Finalized = true, Month = $"{month.Year:D4}-{month.Month:D2}" });
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"HistoryService.FinalizeMonth({month})", ex);
        }
    }

    /// <summary>Atomically replaces the whole month file.</summary>
    public void Save(MonthHistory history)
    {
        var path = FilePathFor(DateOnly.Parse(history.Month + "-01"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = JsonSerializer.Serialize(history, Options);
        var tempPath = path + ".tmp";

        try
        {
            File.WriteAllText(tempPath, json);
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                fs.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"HistoryService.Save({history.Month})", ex);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>Lists months that have history files, newest first.</summary>
    public IReadOnlyList<DateOnly> ListMonths()
    {
        try
        {
            if (!Directory.Exists(_historyDirectory)) return [];
            return Directory.GetFiles(_historyDirectory, "*.json")
                .Select(Path.GetFileName)
                .Select(name => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(name), "yyyy-MM", out var m) ? m : default)
                .Where(m => m != default)
                .OrderByDescending(m => m)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLogger.LogError("HistoryService.ListMonths", ex);
            return [];
        }
    }
}
