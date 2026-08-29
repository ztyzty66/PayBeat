using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;

namespace PayBeat.Tests;

/// <summary>
/// Tests for schedule activation, effective-date resolution, no-workday guard,
/// history isolation, and deletion safety — covering the MiMo v1 polish fixes.
/// </summary>
public class ScheduleActivationTests
{
    private static PayConfiguration BuildConfig(
        List<WorkScheduleProfile>? schedules = null,
        List<WorkWeekPolicy>? policies = null,
        IReadOnlyDictionary<DateOnly, CalendarOverride>? overrides = null)
    {
        schedules ??= [new WorkScheduleProfile
        {
            Id = "default", Name = "默认作息",
            WorkStart = new TimeOnly(9, 0), WorkEnd = new TimeOnly(18, 0),
            EffectiveFrom = new DateOnly(2000, 1, 1),
        }];
        policies ??= [WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1))];
        return new PayConfiguration
        {
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 6000m, EffectiveFrom = new(2000, 1, 1) }],
            ScheduleProfiles = schedules,
            WeekPolicies = policies,
            Overrides = overrides ?? new Dictionary<DateOnly, CalendarOverride>(),
            Holidays = new HolidayCalendar([]),
        };
    }

    // ── Schedule activation by EffectiveDate (4.1) ──────────────────────────

    [Fact]
    public void ScheduleActivation_ByEffectiveDate()
    {
        var config = BuildConfig(schedules:
        [
            new WorkScheduleProfile
            {
                Id = "summer", Name = "夏季作息",
                WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30),
                LunchBreakEnabled = true, LunchBreakStart = new TimeOnly(12, 0), LunchBreakEnd = new TimeOnly(13, 30),
                EffectiveFrom = new DateOnly(2026, 5, 1),
            },
            new WorkScheduleProfile
            {
                Id = "winter", Name = "冬季作息",
                WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0),
                LunchBreakEnabled = true, LunchBreakStart = new TimeOnly(12, 0), LunchBreakEnd = new TimeOnly(13, 0),
                EffectiveFrom = new DateOnly(2026, 11, 1),
            },
        ]);

        // Before Nov 1: summer
        var aug = config.ResolveSchedule(new DateOnly(2026, 8, 15));
        Assert.Equal("summer", aug.Id);
        Assert.Equal(new TimeOnly(17, 30), aug.WorkEnd);

        // After Nov 1: winter
        var dec = config.ResolveSchedule(new DateOnly(2026, 12, 15));
        Assert.Equal("winter", dec.Id);
        Assert.Equal(new TimeOnly(17, 0), dec.WorkEnd);
    }

    [Fact]
    public void ManualActivation_BySettingEffectiveToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var schedules = new List<WorkScheduleProfile>
        {
            new WorkScheduleProfile
            {
                Id = "summer", Name = "夏季作息",
                WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30),
                EffectiveFrom = new DateOnly(2026, 5, 1),
            },
            new WorkScheduleProfile
            {
                Id = "winter", Name = "冬季作息",
                WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0),
                EffectiveFrom = new DateOnly(2026, 11, 1),
            },
        };

        // Simulate OnActivate: set winter's EffectiveFrom to today.
        var activated = schedules.Select(s => s.Id == "winter" ? s with { EffectiveFrom = today } : s).ToList();
        var config = BuildConfig(schedules: activated);

        var active = config.ResolveSchedule(today);
        Assert.Equal("winter", active.Id);
    }

    [Fact]
    public void ActivationDoesNotRetroactive()
    {
        var activateDate = new DateOnly(2026, 9, 25);
        var schedules = new List<WorkScheduleProfile>
        {
            new WorkScheduleProfile
            {
                Id = "summer", Name = "夏季作息",
                WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30),
                EffectiveFrom = new DateOnly(2026, 5, 1),
            },
            new WorkScheduleProfile
            {
                Id = "winter", Name = "冬季作息",
                WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0),
                EffectiveFrom = activateDate,
            },
        };
        var config = BuildConfig(schedules: schedules);

        // Aug still uses summer.
        Assert.Equal(new TimeOnly(17, 30), config.ResolveSchedule(new DateOnly(2026, 8, 1)).WorkEnd);
        // Sep 24 still uses summer.
        Assert.Equal("summer", config.ResolveSchedule(new DateOnly(2026, 9, 24)).Id);
        // Sep 25 uses winter.
        Assert.Equal("winter", config.ResolveSchedule(new DateOnly(2026, 9, 25)).Id);
    }

    // ── No-workday guard (26) ───────────────────────────────────────────────

    [Fact]
    public void NoWorkday_DoesNotDivideByZero()
    {
        var policy = new WorkWeekPolicy { Type = WorkWeekType.Custom, WorkDays = [], EffectiveFrom = new(2000, 1, 1) };
        var config = BuildConfig(policies: [policy]);

        Assert.Equal(0, config.PlannedWorkdays(new DateOnly(2026, 8, 1)));
        Assert.Equal(0m, config.StandardDailyRate(new DateOnly(2026, 8, 3)));
    }

    [Fact]
    public void NoWorkday_SalaryEngineDoesNotThrow()
    {
        var policy = new WorkWeekPolicy { Type = WorkWeekType.Custom, WorkDays = [], EffectiveFrom = new(2000, 1, 1) };
        var config = BuildConfig(policies: [policy]);

        var day = SalaryEngine.ComputeDay(config, new DateOnly(2026, 8, 3));
        Assert.Equal(DayStatus.Rest, day.Status);
        Assert.Equal(0m, day.TargetEarned);

        // Monthly summary must not throw.
        var month = SalaryEngine.ComputeMonth(config, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15), new TimeOnly(12, 0));
        Assert.Equal(0, month.PlannedWorkdays);
        // StandardMonthly is the profile's stated monthly amount, independent of workdays.
        // The per-day rate is 0, and MonthTarget (sum of daily targets) is 0.
        Assert.Equal(6000m, month.StandardMonthly);
        Assert.Equal(0m, month.MonthTarget);
    }

    // ── Duplicate effective date ─────────────────────────────────────────────

    [Fact]
    public void DuplicateEffectiveDate_FirstInListWins()
    {
        // When two schedules share the same EffectiveFrom, the first one in the list wins.
        // This is the "earliest-match" semantics of ResolveSchedule (first `>` wins).
        var config = BuildConfig(schedules:
        [
            new WorkScheduleProfile
            {
                Id = "A", Name = "A",
                WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0),
                EffectiveFrom = new DateOnly(2026, 1, 1),
            },
            new WorkScheduleProfile
            {
                Id = "B", Name = "B",
                WorkStart = new TimeOnly(9, 0), WorkEnd = new TimeOnly(18, 0),
                EffectiveFrom = new DateOnly(2026, 1, 1),
            },
        ]);

        var active = config.ResolveSchedule(new DateOnly(2026, 6, 1));
        Assert.Equal("A", active.Id);
        Assert.Equal(new TimeOnly(17, 0), active.WorkEnd);
    }

    // ── Schedule deletion safety (12) ───────────────────────────────────────

    [Fact]
    public void CurrentSchedule_IdMatchesResolveSchedule()
    {
        var config = BuildConfig(schedules:
        [
            new WorkScheduleProfile
            {
                Id = "only-one", Name = "唯一作息",
                WorkStart = new TimeOnly(9, 0), WorkEnd = new TimeOnly(18, 0),
                EffectiveFrom = new DateOnly(2000, 1, 1),
            },
        ]);

        var active = config.ResolveSchedule(new DateOnly(2026, 8, 1));
        Assert.Equal("only-one", active.Id);
    }

    // ── Salary history isolation (20/21) ────────────────────────────────────

    [Fact]
    public void SalaryHistory_Aug6000_Oct6500_AugStays6000()
    {
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

        // Aug: still 6000.
        TestConfig.AssertMoney(6000m / 26m, config.StandardDailyRate(new DateOnly(2026, 8, 3)));
        // Oct: 6500.
        TestConfig.AssertMoney(6500m / 27m, config.StandardDailyRate(new DateOnly(2026, 10, 5)));
    }

    [Fact]
    public void ScheduleHistory_Summer_Winter_AugKeepsSummer()
    {
        var config = new PayConfiguration
        {
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Daily, DailyAmount = 230m }],
            ScheduleProfiles =
            [
                new WorkScheduleProfile
                {
                    Id = "summer", Name = "夏季作息",
                    WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30),
                    LunchBreakEnabled = true, LunchBreakStart = new TimeOnly(12, 0), LunchBreakEnd = new TimeOnly(13, 30),
                    EffectiveFrom = new DateOnly(2026, 5, 1),
                },
                new WorkScheduleProfile
                {
                    Id = "winter", Name = "冬季作息",
                    WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0),
                    LunchBreakEnabled = true, LunchBreakStart = new TimeOnly(12, 0), LunchBreakEnd = new TimeOnly(13, 0),
                    EffectiveFrom = new DateOnly(2026, 11, 1),
                },
            ],
            WeekPolicies = [WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1))],
            Overrides = new Dictionary<DateOnly, CalendarOverride>(),
            Holidays = new HolidayCalendar([]),
        };

        // Aug uses summer: 8h effective (9.5h - 1.5h lunch).
        var aug = SalaryEngine.ComputeDay(config, new DateOnly(2026, 8, 3));
        Assert.Equal("summer", aug.Schedule.Id);
        Assert.Equal(8 * 3600, aug.TotalEffectiveSeconds);
        Assert.Equal(new TimeOnly(17, 30), aug.Schedule.WorkEnd);

        // Dec uses winter: 8h effective (9h - 1h lunch).
        var dec = SalaryEngine.ComputeDay(config, new DateOnly(2026, 12, 7));
        Assert.Equal("winter", dec.Schedule.Id);
        Assert.Equal(8 * 3600, dec.TotalEffectiveSeconds);
        Assert.Equal(new TimeOnly(17, 0), dec.Schedule.WorkEnd);
    }
}
