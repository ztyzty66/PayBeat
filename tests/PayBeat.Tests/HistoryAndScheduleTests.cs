using PayBeat.App.Domain;

namespace PayBeat.Tests;

/// <summary>History isolation: past months keep their salary AND schedule; month math across boundaries.</summary>
public class HistoryAndScheduleTests
{
    [Fact]
    public void SalaryHistory_Aug6000_Oct6500_AugStays6000()
    {
        // Aug 2026: 6000 (from 2000-01-01); Oct 2026: 6500 (from 2026-10-01).
        var config = new PayConfiguration
        {
            SalaryProfiles =
            [
                new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 6000m, EffectiveFrom = new DateOnly(2000, 1, 1) },
                new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 6500m, EffectiveFrom = new DateOnly(2026, 10, 1) },
            ],
            ScheduleProfiles = [TestConfig.Schedule()],
            WeekPolicies = [WorkWeekPolicy.Create(WorkWeekType.SingleRest, new DateOnly(2000, 1, 1))],
            Overrides = new Dictionary<DateOnly, CalendarOverride>(),
            Holidays = new HolidayCalendar([]),
        };

        var aug1 = new DateOnly(2026, 8, 3);   // Mon in Aug
        var oct1 = new DateOnly(2026, 10, 5);  // Mon in Oct

        TestConfig.AssertMoney(6000m / 26m, config.StandardDailyRate(aug1));
        TestConfig.AssertMoney(6500m / 27m, config.StandardDailyRate(oct1)); // Oct 2026: 31 days, Sundays 4/11/18/25 -> 27 planned days

        var aug = SalaryEngine.ComputeMonth(config, TestConfig.Aug2026, TestConfig.Aug2026, new TimeOnly(0, 0));
        Assert.Equal(6000m, aug.MonthTarget);
        Assert.Equal(6000m, aug.StandardMonthly);
    }

    [Fact]
    public void ScheduleHistory_SummerAug_WinterDec_AugKeepsSummer()
    {
        // Summer schedule (08:00–17:30, lunch 12:00–13:30) from 2000; winter (08:00–17:00, 12:00–13:00) from 2026-11-01.
        var summer = TestConfig.Schedule(
            start: new TimeOnly(8, 0), end: new TimeOnly(17, 30), lunch: true,
            lunchStart: new TimeOnly(12, 0), lunchEnd: new TimeOnly(13, 30));
        var winter = TestConfig.Schedule(
            start: new TimeOnly(8, 0), end: new TimeOnly(17, 0), lunch: true,
            lunchStart: new TimeOnly(12, 0), lunchEnd: new TimeOnly(13, 0)) with
        {
            EffectiveFrom = new DateOnly(2026, 11, 1),
        };

        var config = new PayConfiguration
        {
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Daily, DailyAmount = 230m }],
            ScheduleProfiles = [summer, winter],
            WeekPolicies = [WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1))],
            Overrides = new Dictionary<DateOnly, CalendarOverride>(),
            Holidays = new HolidayCalendar([]),
        };

        var augDay = SalaryEngine.ComputeDay(config, new DateOnly(2026, 8, 3));
        var decDay = SalaryEngine.ComputeDay(config, new DateOnly(2026, 12, 7));

        // Aug still uses summer: 9.5h − 1.5h = 8h effective.
        Assert.Equal(8 * 3600, augDay.TotalEffectiveSeconds);
        // Dec uses winter: 9h − 1h = 8h effective, but window ends 17:00.
        Assert.Equal(8 * 3600, decDay.TotalEffectiveSeconds);
        Assert.Equal(new TimeOnly(17, 0), decDay.Schedule.WorkEnd);
        Assert.Equal(new TimeOnly(17, 30), augDay.Schedule.WorkEnd);

        // Real-time: at 12:30 in Aug we are in lunch (paused); effective spans differ per schedule.
        var augP = SalaryEngine.ComputeDayAt(config, new DateOnly(2026, 8, 3), new TimeOnly(12, 30));
        Assert.Equal(DayPhase.Lunch, augP.Phase);
    }

    [Fact]
    public void MonthAccrual_PastDaysAtFinal_TodayLive_FutureZero()
    {
        // Aug 2026, "today" = Aug 10 (Mon) at 13:00, no lunch, single rest, 230/day.
        var config = TestConfig.Daily(230m, TestConfig.Week(WorkWeekType.SingleRest));

        // Single rest: Sat works. Past paid days: Aug 1,3,4,5,6,7,8 = 7 x 230 (Aug 2/9 Sundays rest).
        // Today Aug 10 at 13:00 -> 4h of 9h -> 230*4/9.
        var summary = SalaryEngine.ComputeMonth(config, TestConfig.Aug2026, new DateOnly(2026, 8, 10), new TimeOnly(13, 0));
        TestConfig.AssertMoney(7 * 230m + 230m * 4m / 9m, summary.MonthEarned);
        Assert.Equal(7, summary.PassedWorkdays);
    }

    [Fact]
    public void MonthTarget_UsesPlannedDays_NotElapsedDays()
    {
        // Mid-month the target is already the full-month figure (plan-based, not time-proportional).
        var config = TestConfig.Monthly(6000m, TestConfig.Week(WorkWeekType.SingleRest));
        var summary = SalaryEngine.ComputeMonth(config, TestConfig.Aug2026, new DateOnly(2026, 8, 10), new TimeOnly(13, 0));
        Assert.Equal(6000m, summary.MonthTarget);
    }

    [Fact]
    public void Decimal_MonthAccrual_NoDoubleRoundingDrift()
    {
        // 26 days at 6000: sum of per-day rates must land exactly on 6000 (Decimal, no float drift).
        var config = TestConfig.Monthly(6000m, TestConfig.Week(WorkWeekType.SingleRest));
        var summary = SalaryEngine.ComputeMonth(config, TestConfig.Aug2026, new DateOnly(2026, 8, 31), new TimeOnly(23, 59));
        TestConfig.AssertMoney(6000m, summary.MonthEarned);
    }
}
