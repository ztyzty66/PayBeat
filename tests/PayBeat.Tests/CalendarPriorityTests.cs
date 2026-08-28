using PayBeat.App.Domain;

namespace PayBeat.Tests;

/// <summary>Day-status priority: user override &gt; official holiday &gt; week policy &gt; default.</summary>
public class CalendarPriorityTests
{
    private static readonly DateOnly Saturday = new(2026, 8, 8);   // a Saturday
    private static readonly DateOnly Monday = new(2026, 8, 3);     // a Monday

    [Fact]
    public void UserOverride_BeatsOfficialHoliday()
    {
        // Oct 1 2026 is an official holiday; user forces work → user wins.
        var config = TestConfig.Monthly().WithHolidays(new HolidayEntry(new DateOnly(2026, 10, 1), true, "国庆节"));
        config = config with
        {
            Overrides = new Dictionary<DateOnly, CalendarOverride>
            {
                [new DateOnly(2026, 10, 1)] = CalendarOverride.For(new DateOnly(2026, 10, 1), DayStatus.Work),
            },
        };
        Assert.Equal(DayStatus.Work, config.ResolveDayStatus(new DateOnly(2026, 10, 1)));
    }

    [Fact]
    public void UserOverride_BeatsWeekPolicy()
    {
        // Saturday would rest; user forces work.
        var config = TestConfig.Monthly(overrides: [CalendarOverride.For(Saturday, DayStatus.Work)]);
        Assert.Equal(DayStatus.Work, config.ResolveDayStatus(Saturday));
    }

    [Fact]
    public void OfficialHoliday_BeatsWeekPolicy()
    {
        // A Monday that is an official holiday rests even though the policy says work.
        var config = TestConfig.Monthly().WithHolidays(new HolidayEntry(Monday, true, "元旦"));
        Assert.Equal(DayStatus.PublicHoliday, config.ResolveDayStatus(Monday));
    }

    [Fact]
    public void OfficialMakeup_BeatsWeekPolicy()
    {
        // A Saturday that is an official makeup workday works even though the policy rests.
        var config = TestConfig.Monthly().WithHolidays(new HolidayEntry(Saturday, false, "补班"));
        Assert.Equal(DayStatus.MakeupWork, config.ResolveDayStatus(Saturday));
    }

    [Fact]
    public void WeekPolicy_BeatsDefault()
    {
        // Single rest: Saturday works by default (no holiday data).
        var config = TestConfig.Monthly(week: TestConfig.Week(WorkWeekType.SingleRest));
        Assert.Equal(DayStatus.Work, config.ResolveDayStatus(Saturday));
        Assert.Equal(DayStatus.Rest, config.ResolveDayStatus(new DateOnly(2026, 8, 9))); // Sunday
    }

    [Fact]
    public void PlannedWorkdays_IgnoresLeave_KeepsDenominator()
    {
        // Adding a leave override must NOT change planned workdays.
        var baseConfig = TestConfig.Monthly(week: TestConfig.Week(WorkWeekType.SingleRest));
        var withLeave = baseConfig with
        {
            Overrides = new Dictionary<DateOnly, CalendarOverride>
            {
                [Monday] = CalendarOverride.LeaveOverride(Monday, new LeaveRecord(LeaveKind.FullDay)),
            },
        };
        Assert.Equal(baseConfig.PlannedWorkdays(TestConfig.Aug2026), withLeave.PlannedWorkdays(TestConfig.Aug2026));
    }

    [Fact]
    public void Holiday_MustNotBreakComputation_WhenDatasetEmpty()
    {
        // Empty holiday calendar: everything falls back to week policy — core pay still works.
        var config = TestConfig.Monthly(); // no holidays
        Assert.Equal(21, config.PlannedWorkdays(TestConfig.Aug2026));
    }
}
