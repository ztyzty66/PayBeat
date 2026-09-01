using PayBeat.App.Domain;
using PayBeat.App.Services;

namespace PayBeat.Tests;

/// <summary>
/// Release hardening tests: holiday coverage, installer metadata, and data safety.
/// </summary>
public class ReleaseHardeningTests
{
    // ── HolidayCalendar Coverage ───────────────────────────────────────────

    [Fact]
    public void HolidayCalendar_2025_IsCovered()
    {
        var calendar = HolidayService.BuiltIn;
        Assert.True(calendar.CoversYear(2025));
    }

    [Fact]
    public void HolidayCalendar_2026_IsCovered()
    {
        var calendar = HolidayService.BuiltIn;
        Assert.True(calendar.CoversYear(2026));
    }

    [Fact]
    public void HolidayCalendar_2027_IsNotCovered()
    {
        var calendar = HolidayService.BuiltIn;
        Assert.False(calendar.CoversYear(2027));
    }

    [Fact]
    public void HolidayCalendar_InvalidData_HasNoCoverage()
    {
        var calendar = new HolidayCalendar([]);
        Assert.False(calendar.CoversYear(2025));
        Assert.Null(calendar.MinCoveredYear);
        Assert.Null(calendar.MaxCoveredYear);
        Assert.Empty(calendar.CoveredYears);
    }

    [Fact]
    public void HolidayCalendar_CoveredYears_ContainsExpected()
    {
        var calendar = HolidayService.BuiltIn;
        Assert.Contains(2025, calendar.CoveredYears);
        Assert.Contains(2026, calendar.CoveredYears);
        Assert.DoesNotContain(2027, calendar.CoveredYears);
    }

    [Fact]
    public void HolidayCalendar_MinMaxCoveredYear()
    {
        var calendar = HolidayService.BuiltIn;
        Assert.Equal(2025, calendar.MinCoveredYear);
        Assert.Equal(2026, calendar.MaxCoveredYear);
    }

    // ── Calendar Coverage Warning ──────────────────────────────────────────

    [Fact]
    public void Calendar_2027_ShowsCoverageWarning()
    {
        // Create a config that resolves to 2027 dates.
        var calendar = HolidayService.BuiltIn;
        Assert.False(calendar.CoversYear(2027));
        // The warning is UI-level; here we verify the API that drives it.
        Assert.False(calendar.CoversYear(2027));
    }

    [Fact]
    public void Calendar_2026_HidesCoverageWarning()
    {
        var calendar = HolidayService.BuiltIn;
        Assert.True(calendar.CoversYear(2026));
    }

    // ── Manual Override Still Wins ─────────────────────────────────────────

    [Fact]
    public void ManualOverride_StillWinsOutsideCoverage()
    {
        // Even if 2027 is not covered by the holiday calendar,
        // a manual override should still be the highest priority.
        var config = new PayConfiguration
        {
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 6000m, EffectiveFrom = new DateOnly(2000, 1, 1) }],
            ScheduleProfiles = [new WorkScheduleProfile { Id = "default", WorkStart = new TimeOnly(9, 0), WorkEnd = new TimeOnly(18, 0), EffectiveFrom = new DateOnly(2000, 1, 1) }],
            WeekPolicies = [WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1))],
            Overrides = new Dictionary<DateOnly, CalendarOverride>
            {
                [new DateOnly(2027, 1, 1)] = CalendarOverride.For(new DateOnly(2027, 1, 1), DayStatus.PaidTimeOff),
            },
            Holidays = HolidayService.BuiltIn,
        };

        var status = config.ResolveDayStatus(new DateOnly(2027, 1, 1));
        Assert.Equal(DayStatus.PaidTimeOff, status);
    }

    // ── Installer Static Tests ─────────────────────────────────────────────

    [Fact]
    public void Installer_AppId_IsUnchanged()
    {
        var iss = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "installer", "PayBeat.iss"));
        Assert.Contains("{{305F5C59-E76E-43F4-BB60-9C97994A151C}", iss);
    }

    [Fact]
    public void Installer_RepoUrl_IsCorrect()
    {
        var iss = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "installer", "PayBeat.iss"));
        Assert.Contains("https://github.com/ztyzty66/PayBeat", iss);
        Assert.DoesNotContain("tztyty66", iss);
    }

    [Fact]
    public void Installer_DoesNotDeleteRuntimeData()
    {
        var iss = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "installer", "PayBeat.iss"));
        Assert.DoesNotContain("今日薪动", iss);
        // UninstallDelete section should not exist at all.
        Assert.DoesNotContain("[UninstallDelete]", iss);
    }

    [Fact]
    public void Installer_DoesNotDeleteLegacyBackup()
    {
        var iss = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "installer", "PayBeat.iss"));
        // Should not delete %APPDATA%\PayBeat either.
        Assert.DoesNotContain("{userappdata}\\{#AppName}", iss);
    }
}
