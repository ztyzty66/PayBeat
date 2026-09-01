using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;

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

    // ── Calendar Coverage Warning (CalendarViewModel) ──────────────────────

    private static (CalendarViewModel vm, ConfigurationStore store) CreateCalendarVm(DateOnly displayMonth)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PayBeatCoverage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var store = new ConfigurationStore(
            new SettingsService(tempDir),
            new HistoryService(Path.Combine(tempDir, "history")));
        var mainVm = new MainViewModel(store);
        var vm = new CalendarViewModel(store, mainVm);
        // Navigate to the target month using existing API.
        var diff = (displayMonth.Year - vm.DisplayMonth.Year) * 12 + displayMonth.Month - vm.DisplayMonth.Month;
        if (diff > 0) for (var i = 0; i < diff; i++) vm.NextMonth();
        else for (var i = 0; i < -diff; i++) vm.PreviousMonth();
        return (vm, store);
    }

    [Fact]
    public void CalendarViewModel_2027_SetsCoverageWarningTrue()
    {
        var (vm, _) = CreateCalendarVm(new DateOnly(2027, 6, 1));
        Assert.True(vm.HasHolidayCoverageWarning);
    }

    [Fact]
    public void CalendarViewModel_2026_SetsCoverageWarningFalse()
    {
        var (vm, _) = CreateCalendarVm(new DateOnly(2026, 6, 1));
        Assert.False(vm.HasHolidayCoverageWarning);
    }

    [Fact]
    public void CalendarViewModel_2027_WarningTextIsNonEmpty()
    {
        var (vm, _) = CreateCalendarVm(new DateOnly(2027, 1, 1));
        Assert.False(string.IsNullOrEmpty(vm.HolidayCoverageWarning));
    }

    [Fact]
    public void CalendarViewModel_2026_WarningTextIsEmpty()
    {
        var (vm, _) = CreateCalendarVm(new DateOnly(2026, 6, 1));
        Assert.Equal("", vm.HolidayCoverageWarning);
    }

    // ── XAML Binding Verification ──────────────────────────────────────────

    [Fact]
    public void CalendarPageControl_Xaml_ContainsCoverageWarningBinding()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "src", "PayBeat.App", "Views", "CalendarPageControl.xaml"));
        Assert.Contains("HasHolidayCoverageWarning", xaml);
        Assert.Contains("HolidayCoverageWarning", xaml);
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
