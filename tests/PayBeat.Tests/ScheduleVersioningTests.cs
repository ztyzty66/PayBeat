using PayBeat.App.Domain;

namespace PayBeat.Tests;

/// <summary>
/// Tests for ScheduleVersioning domain logic: activate, edit, delete operations
/// on versioned schedule collections with historical preservation invariants.
/// </summary>
public class ScheduleVersioningTests
{
    private static PayConfiguration BuildConfig(List<WorkScheduleProfile> schedules)
    {
        return new PayConfiguration
        {
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 6000m, EffectiveFrom = new(2000, 1, 1) }],
            ScheduleProfiles = schedules,
            WeekPolicies = [WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1))],
            Overrides = new Dictionary<DateOnly, CalendarOverride>(),
            Holidays = new HolidayCalendar([]),
        };
    }

    // ── ReactivateHistoricalSchedule_PreservesOldHistory ───────────────────

    [Fact]
    public void ReactivateHistoricalSchedule_PreservesOldHistory()
    {
        var today = new DateOnly(2026, 9, 1);
        var schedules = new List<WorkScheduleProfile>
        {
            new() { Id = "summer", Name = "夏季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30), EffectiveFrom = new DateOnly(2026, 5, 1) },
            new() { Id = "winter", Name = "冬季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = new DateOnly(2026, 8, 1) },
        };

        var result = ScheduleVersioning.Activate(schedules, "summer", today);

        Assert.NotNull(result);
        // Original 05-01 summer entry must still exist.
        Assert.Contains(result, s => s.Id == "summer" && s.EffectiveFrom == new DateOnly(2026, 5, 1));
        // New today-dated entry must also exist (with a new Id).
        Assert.Contains(result, s => s.Name == "夏季作息" && s.EffectiveFrom == today);
        // Winter entry untouched.
        Assert.Contains(result, s => s.Id == "winter" && s.EffectiveFrom == new DateOnly(2026, 8, 1));
        // Resolve for May still uses summer (05-01 entry).
        var config = BuildConfig(result);
        Assert.Equal(new TimeOnly(17, 30), config.ResolveSchedule(new DateOnly(2026, 6, 1)).WorkEnd);
    }

    // ── EditHistoricalSchedule_CreatesNewVersion_NotRewriteHistory ─────────

    [Fact]
    public void EditHistoricalSchedule_CreatesNewVersion_NotRewriteHistory()
    {
        var today = new DateOnly(2026, 9, 1);
        var schedules = new List<WorkScheduleProfile>
        {
            new() { Id = "summer", Name = "夏季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30), EffectiveFrom = new DateOnly(2026, 5, 1) },
            new() { Id = "winter", Name = "冬季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = new DateOnly(2026, 8, 1) },
        };

        var edited = new WorkScheduleProfile
        {
            Id = "summer",
            Name = "夏季作息(修改)",
            WorkStart = new TimeOnly(7, 30),
            WorkEnd = new TimeOnly(17, 0),
            EffectiveFrom = new DateOnly(2026, 5, 1),
        };

        var result = ScheduleVersioning.Edit(schedules, edited, today);

        // Original historical entry must still be present (new Id assigned).
        Assert.Contains(result, s => s.Name == "夏季作息" && s.WorkStart == new TimeOnly(8, 0) && s.EffectiveFrom == new DateOnly(2026, 5, 1));
        // New version with the edited content must exist.
        Assert.Contains(result, s => s.Name == "夏季作息(修改)" && s.WorkStart == new TimeOnly(7, 30));
        // Winter untouched.
        Assert.Contains(result, s => s.Id == "winter");
        // No duplicate IDs.
        Assert.Equal(result.Count, result.Select(s => s.Id).Distinct().Count());
    }

    // ── DeleteHistoricalSchedule_IsRejected ────────────────────────────────

    [Fact]
    public void DeleteHistoricalSchedule_IsRejected()
    {
        var today = new DateOnly(2026, 9, 1);
        var schedules = new List<WorkScheduleProfile>
        {
            new() { Id = "summer", Name = "夏季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30), EffectiveFrom = new DateOnly(2026, 5, 1) },
            new() { Id = "winter", Name = "冬季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = today },
        };

        var config = BuildConfig(schedules);
        var (success, result) = ScheduleVersioning.Delete(schedules, "summer", today, config);

        Assert.False(success);
        Assert.Equal(schedules.Count, result.Count);
    }

    // ── DeleteActiveSchedule_IsRejected ────────────────────────────────────

    [Fact]
    public void DeleteActiveSchedule_IsRejected()
    {
        var today = new DateOnly(2026, 9, 1);
        var schedules = new List<WorkScheduleProfile>
        {
            new() { Id = "summer", Name = "夏季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30), EffectiveFrom = new DateOnly(2026, 5, 1) },
            new() { Id = "winter", Name = "冬季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = today },
        };

        var config = BuildConfig(schedules);
        var (success, _) = ScheduleVersioning.Delete(schedules, "winter", today, config);

        Assert.False(success);
    }

    // ── DeleteFutureSchedule_IsAllowed ─────────────────────────────────────

    [Fact]
    public void DeleteFutureSchedule_IsAllowed()
    {
        var today = new DateOnly(2026, 9, 1);
        var schedules = new List<WorkScheduleProfile>
        {
            new() { Id = "summer", Name = "夏季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30), EffectiveFrom = new DateOnly(2026, 5, 1) },
            new() { Id = "winter", Name = "冬季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = new DateOnly(2026, 11, 1) },
        };

        var config = BuildConfig(schedules);
        var (success, result) = ScheduleVersioning.Delete(schedules, "winter", today, config);

        Assert.True(success);
        Assert.Single(result);
        Assert.Equal("summer", result[0].Id);
    }

    // ── ActivateFutureSchedule_PreservesPast ───────────────────────────────

    [Fact]
    public void ActivateFutureSchedule_PreservesPast()
    {
        var today = new DateOnly(2026, 9, 1);
        var schedules = new List<WorkScheduleProfile>
        {
            new() { Id = "summer", Name = "夏季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30), EffectiveFrom = new DateOnly(2026, 5, 1) },
            new() { Id = "winter", Name = "冬季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = new DateOnly(2026, 11, 1) },
        };

        var result = ScheduleVersioning.Activate(schedules, "winter", today);

        Assert.NotNull(result);
        // Summer at 05-01 preserved.
        Assert.Contains(result, s => s.Id == "summer" && s.EffectiveFrom == new DateOnly(2026, 5, 1));
        // Winter now active from today (new Id, future entry removed).
        Assert.Contains(result, s => s.Name == "冬季作息" && s.EffectiveFrom == today);
        // Original future-dated winter entry removed (superseded by today entry).
        Assert.DoesNotContain(result, s => s.Id == "winter" && s.EffectiveFrom == new DateOnly(2026, 11, 1));
        // Total: summer(05-01) + winter(today) = 2 entries.
        Assert.Equal(2, result.Count);
    }

    // ── SameDayActivation_HasSingleWinner ──────────────────────────────────

    [Fact]
    public void SameDayActivation_HasSingleWinner()
    {
        var today = new DateOnly(2026, 9, 1);
        var schedules = new List<WorkScheduleProfile>
        {
            new() { Id = "summer", Name = "夏季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30), EffectiveFrom = today },
        };

        var result = ScheduleVersioning.Activate(schedules, "summer", today);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("summer", result[0].Id);
    }

    // ── Restart_HistoryStillResolvesSameSchedule ───────────────────────────

    [Fact]
    public void Restart_HistoryStillResolvesSameSchedule()
    {
        var schedules = new List<WorkScheduleProfile>
        {
            new() { Id = "summer", Name = "夏季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 30), EffectiveFrom = new DateOnly(2026, 5, 1) },
            new() { Id = "winter", Name = "冬季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = new DateOnly(2026, 11, 1) },
        };

        // Activate summer from today, then verify past months still resolve correctly.
        var today = new DateOnly(2026, 9, 1);
        var result = ScheduleVersioning.Activate(schedules, "summer", today)!;
        var config = BuildConfig(result);

        // June still uses summer (05-01 entry).
        Assert.Equal(new TimeOnly(17, 30), config.ResolveSchedule(new DateOnly(2026, 6, 15)).WorkEnd);
        // September uses summer (activated today with new Id).
        Assert.Equal(new TimeOnly(17, 30), config.ResolveSchedule(today).WorkEnd);
        // December uses winter (11-01 entry).
        Assert.Equal(new TimeOnly(17, 0), config.ResolveSchedule(new DateOnly(2026, 12, 15)).WorkEnd);
    }
}
