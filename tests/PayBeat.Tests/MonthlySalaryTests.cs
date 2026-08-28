using PayBeat.App.Domain;

namespace PayBeat.Tests;

/// <summary>Monthly-salary mode: daily-rate derivation, workweek presets, leave must not re-average.</summary>
public class MonthlySalaryTests
{
    [Fact]
    public void DoubleRest_Aug2026_PlannedWorkdays_Is21()
    {
        var config = TestConfig.Monthly(6000m);
        Assert.Equal(21, config.PlannedWorkdays(TestConfig.Aug2026));
    }

    [Fact]
    public void SingleRest_Aug2026_PlannedWorkdays_Is26()
    {
        var config = TestConfig.Monthly(6000m, TestConfig.Week(WorkWeekType.SingleRest));
        Assert.Equal(26, config.PlannedWorkdays(TestConfig.Aug2026));
    }

    [Fact]
    public void Monthly_DailyRate_IsMonthlyOverPlanned_26Days()
    {
        // 6000 / 26 = 230.769230... (the reference-example figure ¥230.77)
        var config = TestConfig.Monthly(6000m, TestConfig.Week(WorkWeekType.SingleRest));
        var rate = config.StandardDailyRate(new DateOnly(2026, 8, 3)); // a Monday
        TestConfig.AssertMoney(6000m / 26m, rate);
        TestConfig.AssertMoney(230.7692307692307692307692308m, rate);
    }

    [Fact]
    public void CustomWeek_WednesdayRest_ExcludesWednesdays()
    {
        var week = TestConfig.Week(WorkWeekType.Custom, [
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
        ]);
        var config = TestConfig.Monthly(6000m, week);

        Assert.Equal(DayStatus.Rest, config.ResolveDayStatus(new DateOnly(2026, 8, 5)));  // Wed
        Assert.Equal(DayStatus.Work, config.ResolveDayStatus(new DateOnly(2026, 8, 8)));  // Sat
        Assert.Equal(22, config.PlannedWorkdays(TestConfig.Aug2026)); // Aug 2026 has 5 Mon + 5 Sat here
    }

    [Fact]
    public void Leave_DoesNotReAverage_DailyRate()
    {
        // Full-day leave on one of 26 planned days: rate MUST stay 6000/26, target drops by exactly one rate.
        var leaveDay = new DateOnly(2026, 8, 3);
        var config = TestConfig.Monthly(
            6000m,
            TestConfig.Week(WorkWeekType.SingleRest),
            overrides: [CalendarOverride.LeaveOverride(leaveDay, new LeaveRecord(LeaveKind.FullDay))]);

        Assert.Equal(26, config.PlannedWorkdays(TestConfig.Aug2026));
        Assert.Equal(DayStatus.Leave, config.ResolveDayStatus(leaveDay));

        var day = SalaryEngine.ComputeDay(config, leaveDay);
        TestConfig.AssertMoney(6000m / 26m, day.DailyRate);
        Assert.Equal(0m, day.TargetEarned);
        Assert.Equal(0m, day.FinalEarned);

        var summary = SalaryEngine.ComputeMonth(config, TestConfig.Aug2026, new DateOnly(2026, 8, 31), new TimeOnly(23, 59));
        TestConfig.AssertMoney(6000m - 6000m / 26m, summary.MonthTarget);
        Assert.Equal(9m, decimal.Floor(summary.LeaveHours)); // 9h leave on a 09:00–18:00 no-lunch day
    }

    [Fact]
    public void Monthly_NoLeave_TargetEqualsMonthly()
    {
        var config = TestConfig.Monthly(6000m, TestConfig.Week(WorkWeekType.SingleRest));
        var summary = SalaryEngine.ComputeMonth(config, TestConfig.Aug2026, new DateOnly(2026, 8, 1), new TimeOnly(0, 0));
        Assert.Equal(6000m, summary.MonthTarget);
        Assert.Equal(6000m, summary.StandardMonthly);
    }

    [Fact]
    public void Monthly_PtoDays_DoNotReduceMonthTarget()
    {
        // One PTO day among 26 planned: target stays 6000 (PTO pays in full), PTO counted.
        var ptoDay = new DateOnly(2026, 8, 3);
        var config = TestConfig.Monthly(
            6000m,
            TestConfig.Week(WorkWeekType.SingleRest),
            overrides: [CalendarOverride.For(ptoDay, DayStatus.PaidTimeOff)]);

        var summary = SalaryEngine.ComputeMonth(config, TestConfig.Aug2026, new DateOnly(2026, 8, 31), new TimeOnly(23, 59));
        Assert.Equal(6000m, summary.MonthTarget);
        Assert.Equal(1, summary.PtoDays);
        Assert.Equal(6000m, summary.MonthEarned); // month fully elapsed, PTO day credited
    }

    [Theory]
    [InlineData("2026-01", 22)]  // Jan 2026: 31 days, Thu start, 10 weekend days
    [InlineData("2026-04", 22)]  // Apr 2026: 30 days, Wed start, 8 weekend days
    [InlineData("2026-12", 23)]  // Dec 2026: 31 days, Tue start, 8 weekend days
    public void MonthBoundaries_PlannedWorkdays(string month, int expected)
    {
        var m = DateOnly.Parse(month + "-01");
        var config = TestConfig.Monthly(6000m);
        Assert.Equal(expected, config.PlannedWorkdays(m));
    }

    [Fact]
    public void LeapYear_Feb2024_PlannedWorkdays()
    {
        var config = TestConfig.Monthly(6000m);
        // Feb 2024 (leap): Feb 1 = Thursday. Weekends: 3,4,10,11,17,18,24,25 → 8 rest days → 21 workdays.
        Assert.Equal(21, config.PlannedWorkdays(new DateOnly(2024, 2, 1)));
    }

    [Fact]
    public void NonLeap_Feb2026_PlannedWorkdays()
    {
        var config = TestConfig.Monthly(6000m);
        // Feb 2026 (28 days): Feb 1 = Sunday. Weekends: 1,7,8,14,15,21,22,28 → 8 → 20 workdays.
        Assert.Equal(20, config.PlannedWorkdays(new DateOnly(2026, 2, 1)));
    }
}
