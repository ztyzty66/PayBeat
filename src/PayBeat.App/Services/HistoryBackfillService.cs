using PayBeat.App.Domain;

namespace PayBeat.App.Services;

/// <summary>
/// Backfills missing history snapshots for days that were missed while the app was not running.
/// Called at startup and on resume across midnight. Processes each day individually so that
/// every completed day gets its own snapshot using that day's effective configuration.
/// </summary>
public static class HistoryBackfillService
{
    /// <summary>
    /// Scans history and backfills any gap between the latest recorded date and yesterday.
    /// Does NOT snapshot today (it's not yet complete). Uses the given config to compute
    /// each day's result. Idempotent: re-running produces no duplicates.
    /// </summary>
    /// <param name="history">The history service for reading/writing month files.</param>
    /// <param name="config">Current pay configuration (used for day computation).</param>
    /// <param name="today">Current date. Days strictly before today are backfilled.</param>
    public static void Backfill(HistoryService history, PayConfiguration config, DateOnly today)
    {
        try
        {
            var latestDate = FindLatestHistoryDate(history);
            var startDate = DetermineStartDate(history, today);

            // Backfill from startDate to yesterday (today is not yet complete).
            var day = startDate;
            while (day < today)
            {
                SnapshotDay(history, config, day);

                // Finalize the month if this is the last day.
                var lastOfMonth = new DateOnly(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month));
                if (day == lastOfMonth)
                {
                    FinalizeMonth(history, config, day);
                }

                day = day.AddDays(1);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("HistoryBackfillService.Backfill", ex);
        }
    }

    /// <summary>
    /// Finds the latest date that has a history record across all month files.
    /// Returns null if no history exists at all.
    /// </summary>
    private static DateOnly? FindLatestHistoryDate(HistoryService history)
    {
        DateOnly? latest = null;
        foreach (var month in history.ListMonths())
        {
            var loaded = history.Load(month);
            if (loaded is null) continue;
            foreach (var key in loaded.Days.Keys)
            {
                if (DateOnly.TryParseExact(key, "yyyy-MM-dd", out var date))
                {
                    if (latest is null || date > latest.Value)
                        latest = date;
                }
            }
        }
        return latest;
    }

    /// <summary>
    /// Determines where to start backfilling. If no history exists, starts from the
    /// first day of the current month. Otherwise starts from the day after the latest
    /// recorded date.
    /// </summary>
    private static DateOnly DetermineStartDate(HistoryService history, DateOnly today)
    {
        var latest = FindLatestHistoryDate(history);
        if (latest is null)
        {
            // No history at all: start from the first day of the current month.
            return new DateOnly(today.Year, today.Month, 1);
        }

        // Start from the day after the latest recorded date.
        return latest.Value.AddDays(1);
    }

    private static void SnapshotDay(HistoryService history, PayConfiguration config, DateOnly date)
    {
        var day = SalaryEngine.ComputeDay(config, date);
        history.RecordDay(date, new DayHistoryRecord
        {
            Date = date,
            Status = day.Status,
            DailyRate = day.DailyRate,
            TargetEarned = day.TargetEarned,
            FinalEarned = day.FinalEarned,
            LeaveSeconds = day.LeaveSeconds,
            SalaryProfileSnapshot = config.ResolveSalaryProfile(date),
            ScheduleSnapshot = day.Schedule,
            WeekPolicySnapshot = config.ResolveWeekPolicy(date),
            PlannedWorkdaysSnapshot = config.PlannedWorkdays(date),
        });
    }

    private static void FinalizeMonth(HistoryService history, PayConfiguration config, DateOnly lastDay)
    {
        var summary = SalaryEngine.ComputeMonth(config, lastDay, lastDay.AddDays(1), new TimeOnly(0, 0));
        var existing = history.Load(lastDay) ?? new MonthHistory { Month = $"{lastDay.Year:D4}-{lastDay.Month:D2}" };
        history.FinalizeMonth(lastDay, existing with
        {
            StandardMonthlySnapshot = summary.StandardMonthly,
            MonthTargetSnapshot = summary.MonthTarget,
            MonthEarnedSnapshot = summary.MonthEarned,
            PlannedWorkdays = summary.PlannedWorkdays,
            PassedWorkdaysSnapshot = summary.PassedWorkdays,
            PtoDays = summary.PtoDays,
            LeaveHours = summary.LeaveHours,
            WorkWeekTypeSnapshot = config.ResolveWeekPolicy(lastDay).Type.ToString(),
        });
    }
}
