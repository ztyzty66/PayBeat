using PayBeat.App.Domain;

namespace PayBeat.Tests;

/// <summary>
/// Tests for leave logic when LunchBreakEnabled = false: morning/afternoon split
/// at the effective-work-seconds midpoint, and hourly leave validation.
/// </summary>
public class NoLunchLeaveTests
{
    private static readonly DateOnly Workday = new(2026, 8, 3); // Monday

    private static PayConfiguration Config(LeaveRecord leave) =>
        TestConfig.Daily(
            230m,
            schedule: TestConfig.Schedule(
                start: new TimeOnly(9, 0),
                end: new TimeOnly(18, 0),
                lunch: false),
            overrides: [CalendarOverride.LeaveOverride(Workday, leave)]);

    // ── MorningLeave_NoLunch_IsFirstHalf ───────────────────────────────────

    [Fact]
    public void MorningLeave_NoLunch_IsFirstHalf()
    {
        // No lunch: 09:00–18:00 = 9h effective. Morning = first 4.5h (ceil) = 4h30m.
        var day = SalaryEngine.ComputeDay(Config(new LeaveRecord(LeaveKind.Morning)), Workday);
        // Morning = 09:00–13:30 = 4.5h = 16200s.
        Assert.Equal(4.5 * 3600, day.LeaveSeconds);
        // LeaveDeduction = 230 × 4.5/9 = 115.
        TestConfig.AssertMoney(115m, day.LeaveDeduction);
        TestConfig.AssertMoney(115m, day.TargetEarned);
    }

    // ── AfternoonLeave_NoLunch_IsSecondHalf ────────────────────────────────

    [Fact]
    public void AfternoonLeave_NoLunch_IsSecondHalf()
    {
        // No lunch: 09:00–18:00 = 9h effective. Afternoon = last 4.5h (floor) = 13:30–18:00.
        var day = SalaryEngine.ComputeDay(Config(new LeaveRecord(LeaveKind.Afternoon)), Workday);
        // Afternoon = 13:30–18:00 = 4.5h = 16200s.
        Assert.Equal(4.5 * 3600, day.LeaveSeconds);
        TestConfig.AssertMoney(115m, day.LeaveDeduction);
        TestConfig.AssertMoney(115m, day.TargetEarned);
    }

    // ── MorningPlusAfternoon_EqualsFullDay ─────────────────────────────────

    [Fact]
    public void MorningPlusAfternoon_NoLunch_EqualsFullDay()
    {
        // Simulate both morning and afternoon leave on separate days to verify total coverage.
        var morning = SalaryEngine.ComputeDay(Config(new LeaveRecord(LeaveKind.Morning)), Workday);
        var afternoon = SalaryEngine.ComputeDay(Config(new LeaveRecord(LeaveKind.Afternoon)), Workday);

        // Combined = full day effective (4.5h + 4.5h = 9h).
        Assert.Equal(9 * 3600, morning.LeaveSeconds + afternoon.LeaveSeconds);
        // Combined deduction = full daily rate.
        TestConfig.AssertMoney(230m, morning.LeaveDeduction + afternoon.LeaveDeduction);
    }

    // ── HourlyLeave_StartAfterEnd_Rejected ─────────────────────────────────

    [Fact]
    public void HourlyLeave_StartAfterEnd_Rejected()
    {
        var schedule = TestConfig.Schedule(start: new TimeOnly(9, 0), end: new TimeOnly(18, 0), lunch: false);
        var leave = new LeaveRecord(LeaveKind.Hours, new TimeOnly(14, 0), new TimeOnly(10, 0));
        var error = leave.Validate(schedule, key => key);
        Assert.NotNull(error);
        Assert.Contains("LeaveStartAfterEnd", error);
    }

    // ── HourlyLeave_OutsideWorkHours_Rejected ──────────────────────────────

    [Fact]
    public void HourlyLeave_OutsideWorkHours_Rejected()
    {
        var schedule = TestConfig.Schedule(start: new TimeOnly(9, 0), end: new TimeOnly(18, 0), lunch: false);
        var leave = new LeaveRecord(LeaveKind.Hours, new TimeOnly(19, 0), new TimeOnly(20, 0));
        var error = leave.Validate(schedule, key => key);
        Assert.NotNull(error);
        Assert.Contains("LeaveOutsideWorkHours", error);
    }

    // ── HourlyLeave_CrossLunch_RemainsCorrect ──────────────────────────────

    [Fact]
    public void HourlyLeave_CrossLunch_RemainsCorrect()
    {
        // With lunch: 11:30–13:30 over lunch 12:00–13:00 → effective = 30min + 30min = 1h.
        var config = TestConfig.Daily(230m,
            schedule: TestConfig.Schedule(start: new TimeOnly(9, 0), end: new TimeOnly(18, 0), lunch: true),
            overrides: [CalendarOverride.LeaveOverride(Workday, new LeaveRecord(LeaveKind.Hours, new TimeOnly(11, 30), new TimeOnly(13, 30)))]);
        var day = SalaryEngine.ComputeDay(config, Workday);
        Assert.Equal(3600, day.LeaveSeconds);
    }

    // ── MorningLeave_WithLunch_CorrectShare (existing behavior preserved) ──

    [Fact]
    public void MorningLeave_WithLunch_CorrectShare()
    {
        // With lunch: morning = 09:00–12:00 = 3h.
        var config = TestConfig.Daily(230m,
            schedule: TestConfig.Schedule(start: new TimeOnly(9, 0), end: new TimeOnly(18, 0), lunch: true),
            overrides: [CalendarOverride.LeaveOverride(Workday, new LeaveRecord(LeaveKind.Morning))]);
        var day = SalaryEngine.ComputeDay(config, Workday);
        Assert.Equal(3 * 3600, day.LeaveSeconds);
        TestConfig.AssertMoney(86.25m, day.LeaveDeduction);
    }
}
