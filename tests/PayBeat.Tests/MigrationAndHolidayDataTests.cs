using PayBeat.App.Domain;
using PayBeat.App.Services;
using PayBeat.App.Models;

namespace PayBeat.Tests;

/// <summary>Legacy settings migration + embedded holiday dataset sanity.</summary>
public class MigrationAndHolidayDataTests
{
    [Fact]
    public void LegacySettings_MigrateToV2()
    {
        var legacy = new SalarySettings
        {
            ConfigVersion = 1,
            DailySalary = 500m,
            WorkStart = new TimeOnly(8, 30),
            WorkEnd = new TimeOnly(17, 30),
            LunchBreakEnabled = true,
            LunchBreakStart = new TimeOnly(12, 0),
            LunchBreakEnd = new TimeOnly(13, 0),
            WorkOnWeekends = false,
        };

        var migrated = SettingsService.Migrate(legacy);

        Assert.Equal(2, migrated.ConfigVersion);
        var salary = Assert.Single(migrated.SalaryProfiles);
        Assert.Equal(SalaryMode.Daily, salary.Mode);
        Assert.Equal(500m, salary.DailyAmount);

        var schedule = Assert.Single(migrated.ScheduleProfiles);
        Assert.Equal(new TimeOnly(8, 30), schedule.WorkStart);
        Assert.Equal(new TimeOnly(17, 30), schedule.WorkEnd);
        Assert.True(schedule.LunchBreakEnabled);

        var policy = Assert.Single(migrated.WeekPolicies);
        Assert.Equal(WorkWeekType.DoubleRest, policy.Type);
        Assert.True(migrated.SetupCompleted);
    }

    [Fact]
    public void LegacySettings_WeekendWork_MigratesToAllDays()
    {
        var legacy = new SalarySettings { ConfigVersion = 1, WorkOnWeekends = true };
        var migrated = SettingsService.Migrate(legacy);
        var policy = Assert.Single(migrated.WeekPolicies);
        Assert.Equal(7, policy.WorkDays.Count);
    }

    [Fact]
    public void Migration_IsIdempotent()
    {
        var legacy = new SalarySettings { ConfigVersion = 1, DailySalary = 300m };
        var once = SettingsService.Migrate(legacy);
        var twice = SettingsService.Migrate(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void EmbeddedHolidayDataset_LoadsAndCovers2026()
    {
        var calendar = HolidayService.BuiltIn;

        Assert.Equal(DayStatus.PublicHoliday, Resolve(calendar, new DateOnly(2026, 10, 1)));  // National Day off
        Assert.Equal(DayStatus.MakeupWork, Resolve(calendar, new DateOnly(2026, 10, 10)));    // Sat makeup work
        Assert.Equal(DayStatus.PublicHoliday, Resolve(calendar, new DateOnly(2026, 2, 17)));  // Spring Festival
        Assert.Equal(DayStatus.MakeupWork, Resolve(calendar, new DateOnly(2026, 2, 14)));     // Sat makeup work
        Assert.Equal(DayStatus.PublicHoliday, Resolve(calendar, new DateOnly(2025, 10, 1)));  // 2025 dataset present
        Assert.Null(calendar.Get(new DateOnly(2027, 1, 1)));                                  // 2027 not shipped
    }

    [Fact]
    public void MalformedHolidayJson_YieldsEmptyCalendar_NotThrow()
    {
        var calendar = HolidayCalendar.FromJson("{ not valid json !!!");
        Assert.Empty(calendar.All);
    }

    private static DayStatus? Resolve(HolidayCalendar calendar, DateOnly date) =>
        calendar.Get(date) is { } e ? (e.IsOffDay ? DayStatus.PublicHoliday : DayStatus.MakeupWork) : null;
}
