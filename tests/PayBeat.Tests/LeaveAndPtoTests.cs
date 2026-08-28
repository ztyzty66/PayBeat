using PayBeat.App.Domain;

namespace PayBeat.Tests;

/// <summary>Leave deductions (full/morning/afternoon/hours/cross-lunch) and PTO semantics.</summary>
public class LeaveAndPtoTests
{
    private static readonly DateOnly Workday = new(2026, 8, 3); // Monday, 09:00–18:00, 9h effective

    private static PayConfiguration Config(LeaveRecord leave, bool lunch = false) =>
        TestConfig.Daily(
            230m,
            schedule: TestConfig.Schedule(
                start: new TimeOnly(9, 0),
                end: new TimeOnly(18, 0),
                lunch: lunch),
            overrides: [CalendarOverride.LeaveOverride(Workday, leave)]);

    [Fact]
    public void FullDayLeave_TargetZero()
    {
        var day = SalaryEngine.ComputeDay(Config(new LeaveRecord(LeaveKind.FullDay)), Workday);
        Assert.Equal(230m, day.LeaveDeduction);
        Assert.Equal(0m, day.TargetEarned);
        Assert.Equal(9 * 3600, day.LeaveSeconds);
    }

    [Fact]
    public void MorningLeave_NoLunch_HalfDeduction()
    {
        // No lunch: morning = full 09:00–18:00 → 9h deducted → target 0. With lunch 12–13: morning = 3h.
        var withLunch = Config(new LeaveRecord(LeaveKind.Morning), lunch: true);
        var day = SalaryEngine.ComputeDay(withLunch, Workday);
        Assert.Equal(3 * 3600, day.LeaveSeconds);
        Assert.Equal(86.25m, day.LeaveDeduction);  // 230 x 3/8 (8h effective: 9h - 1h lunch)
        Assert.Equal(143.75m, day.TargetEarned);
    }

    [Fact]
    public void AfternoonLeave_WithLunch_CorrectShare()
    {
        var withLunch = Config(new LeaveRecord(LeaveKind.Afternoon), lunch: true);
        var day = SalaryEngine.ComputeDay(withLunch, Workday);
        Assert.Equal(5 * 3600, day.LeaveSeconds);
        Assert.Equal(143.75m, day.LeaveDeduction);
        Assert.Equal(86.25m, day.TargetEarned);
    }

    [Fact]
    public void HourlyLeave_OneHour()
    {
        var day = SalaryEngine.ComputeDay(Config(new LeaveRecord(LeaveKind.Hours, new TimeOnly(10, 0), new TimeOnly(11, 0))), Workday);
        Assert.Equal(3600, day.LeaveSeconds);
        TestConfig.AssertMoney(230m / 9m, day.LeaveDeduction);
        TestConfig.AssertMoney(230m * 8m / 9m, day.TargetEarned);
    }

    [Fact]
    public void HourlyLeave_CrossingLunch_OnlyEffectiveHoursDeducted()
    {
        // 11:30–13:30 request over lunch 12:00–13:00 → effective = 30min + 30min = 1h only.
        var withLunch = Config(new LeaveRecord(LeaveKind.Hours, new TimeOnly(11, 30), new TimeOnly(13, 30)), lunch: true);
        var day = SalaryEngine.ComputeDay(withLunch, Workday);
        Assert.Equal(3600, day.LeaveSeconds);                 // NOT 2h
        Assert.Equal(28.75m, day.LeaveDeduction);
    }

    [Fact]
    public void HourlyLeave_EdgesClampedToWorkWindow()
    {
        // Request 08:00–10:00 (starts before work) → only 09:00–10:00 counts = 1h.
        var day = SalaryEngine.ComputeDay(Config(new LeaveRecord(LeaveKind.Hours, new TimeOnly(8, 0), new TimeOnly(10, 0))), Workday);
        Assert.Equal(3600, day.LeaveSeconds);
    }

    [Fact]
    public void HourlyLeave_RealtimeSkipsLeaveWindow()
    {
        // 1h leave 10:00–11:00. At 12:00: worked = (10−9) + (12−11) = 2h → earned = 230×2/9.
        var config = Config(new LeaveRecord(LeaveKind.Hours, new TimeOnly(10, 0), new TimeOnly(11, 0)));
        var p = SalaryEngine.ComputeDayAt(config, Workday, new TimeOnly(12, 0));
        TestConfig.AssertMoney(230m * 2m / 9m, p.Earned);
    }

    [Fact]
    public void HourlyLeave_EndOfDayReachesTarget()
    {
        // After work, earned must land exactly on target (daily rate − deduction).
        var config = Config(new LeaveRecord(LeaveKind.Hours, new TimeOnly(10, 0), new TimeOnly(11, 30)));
        var day = SalaryEngine.ComputeDay(config, Workday);
        var p = SalaryEngine.ComputeDayAt(config, Workday, new TimeOnly(18, 30));
        Assert.Equal(day.TargetEarned, p.Earned);
        Assert.Equal(DayPhase.AfterWork, p.Phase);
    }

    [Fact]
    public void Pto_FullCredit_AtAnyMoment()
    {
        var config = TestConfig.Daily(230m, overrides: [CalendarOverride.For(Workday, DayStatus.PaidTimeOff)]);
        var p = SalaryEngine.ComputeDayAt(config, Workday, new TimeOnly(10, 15));
        Assert.Equal(DayPhase.PaidTimeOff, p.Phase);
        Assert.Equal(230m, p.Earned);
        Assert.Equal(1.0, p.Progress);
    }

    [Fact]
    public void Pto_Semantics_MonthTargetUnchanged_EarnedCredited()
    {
        var config = TestConfig.Monthly(
            6000m,
            TestConfig.Week(WorkWeekType.SingleRest),
            overrides: [CalendarOverride.For(Workday, DayStatus.PaidTimeOff)]);
        var summary = SalaryEngine.ComputeMonth(config, TestConfig.Aug2026, Workday, new TimeOnly(10, 0));

        TestConfig.AssertMoney(6000m, summary.MonthTarget);
        // Single rest: Aug 1 (Sat) is a paid day that already passed; Aug 2 (Sun) rests.
        // Today Aug 3 is PTO -> full rate. Total = 1 past paid day + today's PTO credit.
        TestConfig.AssertMoney(6000m / 26m * 2m, summary.MonthEarned);
        Assert.Equal(1, summary.PtoDays);
    }
}
