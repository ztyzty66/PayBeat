using PayBeat.App.Domain;

namespace PayBeat.Tests;

/// <summary>Daily-salary mode and real-time accrual incl. lunch-break boundaries.</summary>
public class DailyAndRealtimeTests
{
    private static readonly DateOnly Workday = new(2026, 8, 3); // Monday

    [Fact]
    public void Daily_BeforeWork_EarnsZero()
    {
        var progress = SalaryEngine.ComputeDayAt(TestConfig.Daily(230m), Workday, new TimeOnly(8, 59));
        Assert.Equal(DayPhase.BeforeWork, progress.Phase);
        Assert.Equal(0m, progress.Earned);
        Assert.Equal(0, progress.WorkedSeconds);
        Assert.Equal(9 * 3600, progress.RemainingSeconds); // 09:00→18:00 wall clock
    }

    [Fact]
    public void Daily_AfterWork_CapsExactlyAtDaily()
    {
        var progress = SalaryEngine.ComputeDayAt(TestConfig.Daily(230m), Workday, new TimeOnly(18, 0));
        Assert.Equal(DayPhase.AfterWork, progress.Phase);
        Assert.Equal(230m, progress.Earned);
        Assert.Equal(1.0, progress.Progress);
        Assert.Equal(0, progress.RemainingSeconds);
    }

    [Fact]
    public void Daily_AfterWork_18ToMidnight_DoesNotGrow()
    {
        var at18 = SalaryEngine.ComputeDayAt(TestConfig.Daily(230m), Workday, new TimeOnly(18, 0)).Earned;
        var at23 = SalaryEngine.ComputeDayAt(TestConfig.Daily(230m), Workday, new TimeOnly(23, 0)).Earned;
        Assert.Equal(at18, at23);
    }

    [Fact]
    public void Daily_Midwork_Linear()
    {
        // 4h23m of 9h → 230 × (4×3600+23×60)/32400 = 126.38...
        var progress = SalaryEngine.ComputeDayAt(TestConfig.Daily(230m), Workday, new TimeOnly(13, 23));
        var expected = 230m * (4m * 3600m + 23m * 60m) / 32400m;
        TestConfig.AssertMoney(expected, progress.Earned);
        Assert.Equal(DayPhase.Working, progress.Phase);
    }

    [Fact]
    public void RestDay_EarnsNothing_AnyTime()
    {
        var sunday = new DateOnly(2026, 8, 2);
        var progress = SalaryEngine.ComputeDayAt(TestConfig.Daily(230m), sunday, new TimeOnly(14, 0));
        Assert.Equal(DayPhase.OffDay, progress.Phase);
        Assert.Equal(0m, progress.Earned);
    }

    // lunch boundaries: 08:00-17:00 (9h) with lunch 12:00-13:00 -> 8h effective

    private static PayConfiguration LunchConfig() =>
        TestConfig.Daily(350m, schedule: TestConfig.Schedule(
            start: new TimeOnly(8, 0), end: new TimeOnly(17, 0), lunch: true));

    [Fact]
    public void Lunch_11h59_StillAccruing()
    {
        var p = SalaryEngine.ComputeDayAt(LunchConfig(), Workday, new TimeOnly(11, 59));
        Assert.Equal(DayPhase.Working, p.Phase);
        TestConfig.AssertMoney(350m * (4m * 3600m - 60m) / (8m * 3600m), p.Earned);
    }

    [Fact]
    public void Lunch_12h00_Paused()
    {
        var p = SalaryEngine.ComputeDayAt(LunchConfig(), Workday, new TimeOnly(12, 0));
        Assert.Equal(DayPhase.Lunch, p.Phase);
        Assert.Equal(175m, p.Earned); // froze at 4h of 8h
    }

    [Fact]
    public void Lunch_12h30_PausedAtSameValue()
    {
        var at1200 = SalaryEngine.ComputeDayAt(LunchConfig(), Workday, new TimeOnly(12, 0)).Earned;
        var at1230 = SalaryEngine.ComputeDayAt(LunchConfig(), Workday, new TimeOnly(12, 30)).Earned;
        Assert.Equal(at1200, at1230);
    }

    [Fact]
    public void Lunch_13h00_Resumes()
    {
        var p = SalaryEngine.ComputeDayAt(LunchConfig(), Workday, new TimeOnly(13, 0));
        Assert.Equal(DayPhase.Working, p.Phase);
        Assert.Equal(175m, p.Earned); // same as lunch start (no accrual during break)
    }

    [Fact]
    public void Lunch_16h59_AccruingNearEnd()
    {
        var p = SalaryEngine.ComputeDayAt(LunchConfig(), Workday, new TimeOnly(16, 59));
        Assert.Equal(DayPhase.Working, p.Phase);
        TestConfig.AssertMoney(350m * (8m * 3600m - 60m) / (8m * 3600m), p.Earned);
    }

    [Fact]
    public void Lunch_17h00_Capped()
    {
        var p = SalaryEngine.ComputeDayAt(LunchConfig(), Workday, new TimeOnly(17, 0));
        Assert.Equal(DayPhase.AfterWork, p.Phase);
        Assert.Equal(350m, p.Earned);
        Assert.Equal(1.0, p.Progress);
    }

    [Fact]
    public void Lunch_18h00_StillCapped()
    {
        var p = SalaryEngine.ComputeDayAt(LunchConfig(), Workday, new TimeOnly(18, 0));
        Assert.Equal(350m, p.Earned);
    }

    [Fact]
    public void MakeupWorkday_WeekendOfficialWorkday_Pays()
    {
        // Official makeup workday on a Saturday.
        var sat = new DateOnly(2026, 10, 10);
        var config = TestConfig.Daily(230m).WithHolidays(new HolidayEntry(sat, false, "国庆节补班"));
        Assert.Equal(DayStatus.MakeupWork, config.ResolveDayStatus(sat));
        var p = SalaryEngine.ComputeDayAt(config, sat, new TimeOnly(12, 0));
        TestConfig.AssertMoney(230m * 3m / 9m, p.Earned);
    }

    [Fact]
    public void PublicHoliday_PaysNothing()
    {
        var holiday = new DateOnly(2026, 10, 1);
        var config = TestConfig.Daily(230m).WithHolidays(new HolidayEntry(holiday, true, "国庆节"));
        Assert.Equal(DayStatus.PublicHoliday, config.ResolveDayStatus(holiday));
        Assert.Equal(0m, SalaryEngine.ComputeDayAt(config, holiday, new TimeOnly(12, 0)).Earned);
    }
}
