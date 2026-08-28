using PayBeat.App.Domain;

namespace PayBeat.Tests;

/// <summary>Shared fixture helpers for building pay configurations.</summary>
public static class TestConfig
{
    public const string DefaultScheduleId = "default";

    /// <summary>Monthly salary config: double rest, 09:00–18:00, no lunch by default.</summary>
    public static PayConfiguration Monthly(
        decimal monthly = 6000m,
        WorkWeekPolicy? week = null,
        WorkScheduleProfile? schedule = null,
        IReadOnlyList<CalendarOverride>? overrides = null,
        IReadOnlyList<HolidayEntry>? holidays = null)
    {
        schedule ??= Schedule();
        return new PayConfiguration
        {
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = monthly }],
            ScheduleProfiles = [schedule],
            WeekPolicies = [week ?? WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1))],
            Overrides = (overrides ?? []).ToDictionary(o => o.Date, o => o),
            Holidays = new HolidayCalendar(holidays ?? []),
        };
    }

    /// <summary>Daily salary config.</summary>
    public static PayConfiguration Daily(
        decimal daily = 230m,
        WorkWeekPolicy? week = null,
        WorkScheduleProfile? schedule = null,
        IReadOnlyList<CalendarOverride>? overrides = null,
        IReadOnlyList<HolidayEntry>? holidays = null)
    {
        schedule ??= Schedule();
        return new PayConfiguration
        {
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Daily, DailyAmount = daily }],
            ScheduleProfiles = [schedule],
            WeekPolicies = [week ?? WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1))],
            Overrides = (overrides ?? []).ToDictionary(o => o.Date, o => o),
            Holidays = new HolidayCalendar(holidays ?? []),
        };
    }

    public static WorkScheduleProfile Schedule(
        TimeOnly? start = null,
        TimeOnly? end = null,
        bool lunch = false,
        TimeOnly? lunchStart = null,
        TimeOnly? lunchEnd = null) => new()
    {
        Id = DefaultScheduleId,
        Name = "test",
        WorkStart = start ?? new TimeOnly(9, 0),
        WorkEnd = end ?? new TimeOnly(18, 0),
        LunchBreakEnabled = lunch,
        LunchBreakStart = lunchStart ?? new TimeOnly(12, 0),
        LunchBreakEnd = lunchEnd ?? new TimeOnly(13, 0),
    };

    public static WorkWeekPolicy Week(WorkWeekType type, IEnumerable<DayOfWeek>? workDays = null) => new()
    {
        Type = type,
        WorkDays = workDays?.ToHashSet() ?? WorkWeekPolicy.Create(type, new DateOnly(2000, 1, 1)).WorkDays,
    };

    public static PayConfiguration WithHolidays(this PayConfiguration config, params HolidayEntry[] entries) =>
        config with { Holidays = new HolidayCalendar(entries) };

    /// <summary>2026-08: Saturdays 1/8/15/22/29, Sundays 2/9/16/23/30 → 21 workdays (double rest), 26 (single rest).</summary>
    public static readonly DateOnly Aug2026 = new(2026, 8, 1);

    /// <summary>
    /// Money tolerance assertion: repeating decimals (6000/26 etc.) legitimately differ in the
    /// last few of decimal's 28 significant digits depending on the order of operations.
    /// Six decimal places is far beyond the two the UI ever shows.
    /// </summary>
    public static void AssertMoney(decimal expected, decimal actual, int digits = 6) =>
        Assert.True(Math.Abs(expected - actual) < (decimal)Math.Pow(10, -digits),
            $"Expected ≈{expected} but got {actual}");
}
