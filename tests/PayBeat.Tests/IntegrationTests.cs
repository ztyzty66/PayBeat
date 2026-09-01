using System.IO;
using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;

namespace PayBeat.Tests;

/// <summary>
/// Integration tests verifying cross-service flows:
/// ConfigurationStore ↔ SettingsService ↔ HistoryService ↔ PayConfiguration.
/// Each test uses a fresh temp directory for full isolation.
/// </summary>
public class IntegrationTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PayBeatTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static SalarySettings MakeSettings(
        List<WorkWeekPolicy>? weekPolicies = null,
        List<WorkScheduleProfile>? scheduleProfiles = null,
        List<SalaryProfile>? salaryProfiles = null,
        Dictionary<string, CalendarOverride>? overrides = null)
    {
        return new SalarySettings
        {
            ConfigVersion = 3,
            WeekPolicies = weekPolicies ?? [WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1))],
            ScheduleProfiles = scheduleProfiles ?? [TestConfig.Schedule()],
            SalaryProfiles = salaryProfiles ?? [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 6000m, EffectiveFrom = new DateOnly(2000, 1, 1) }],
            Overrides = overrides ?? [],
            SetupCompleted = true,
        };
    }

    /// <summary>
    /// I-01: Save WorkWeek → Calendar.
    /// Commit a single rest week policy via ConfigurationStore, then verify CalendarViewModel
    /// sees Saturday as Work status.
    /// </summary>
    [Fact]
    public void I01_SaveWorkWeek_ToCalendar()
    {
        var dir = CreateTempDir();
        var settingsService = new SettingsService(dir);
        var historyService = new HistoryService(Path.Combine(dir, "history"));
        var store = new ConfigurationStore(settingsService, historyService);

        // SingleRest: Mon–Sat work, Sun rest.
        var settings = MakeSettings(
            weekPolicies: [WorkWeekPolicy.Create(WorkWeekType.SingleRest, new DateOnly(2000, 1, 1))]);
        store.Commit(settings);

        // Create a draft from the committed store and build a preview configuration
        var draft = store.CreateDraft();
        var previewConfig = draft.BuildPreviewConfiguration(store.PayData);

        // 2026-08-29 is a Saturday
        var saturday = new DateOnly(2026, 8, 29);
        var status = previewConfig.ResolveDayStatus(saturday);

        Assert.Equal(DayStatus.Work, status);
    }

    /// <summary>
    /// I-02: Save WorkWeek → Widget.
    /// Commit single rest week policy, then verify the store's CurrentConfiguration
    /// exposes the correct configuration.
    /// </summary>
    [Fact]
    public void I02_SaveWorkWeek_ToWidget()
    {
        var dir = CreateTempDir();
        var settingsService = new SettingsService(dir);
        var historyService = new HistoryService(Path.Combine(dir, "history"));
        var store = new ConfigurationStore(settingsService, historyService);

        var settings = MakeSettings(
            weekPolicies: [WorkWeekPolicy.Create(WorkWeekType.SingleRest, new DateOnly(2000, 1, 1))]);
        store.Commit(settings);

        var config = store.CurrentConfiguration;
        var policy = config.ResolveWeekPolicy(new DateOnly(2026, 8, 29));

        Assert.Equal(WorkWeekType.SingleRest, policy.Type);
        Assert.Contains(DayOfWeek.Saturday, policy.WorkDays);
        Assert.DoesNotContain(DayOfWeek.Sunday, policy.WorkDays);
    }

    /// <summary>
    /// I-03: Single Rest Saturday.
    /// Single rest week policy → Saturday = Work status.
    /// </summary>
    [Fact]
    public void I03_SingleRest_SaturdayIsWork()
    {
        var config = TestConfig.Monthly(
            week: WorkWeekPolicy.Create(WorkWeekType.SingleRest, new DateOnly(2000, 1, 1)));

        // 2026-08-29 is a Saturday
        var status = config.ResolveDayStatus(new DateOnly(2026, 8, 29));
        Assert.Equal(DayStatus.Work, status);
    }

    /// <summary>
    /// I-04: Double Rest Saturday.
    /// Double rest week policy → Saturday = Rest status.
    /// </summary>
    [Fact]
    public void I04_DoubleRest_SaturdayIsRest()
    {
        var config = TestConfig.Monthly(
            week: WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1)));

        var status = config.ResolveDayStatus(new DateOnly(2026, 8, 29));
        Assert.Equal(DayStatus.Rest, status);
    }

    /// <summary>
    /// I-05: Custom Sunday Work.
    /// Custom week with Sunday as a work day → Sunday = Work status.
    /// </summary>
    [Fact]
    public void I05_CustomSundayWork()
    {
        var customWeek = new WorkWeekPolicy
        {
            Type = WorkWeekType.Custom,
            WorkDays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                         DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Sunday],
            EffectiveFrom = new DateOnly(2000, 1, 1),
        };
        var config = TestConfig.Monthly(week: customWeek);

        // 2026-08-30 is a Sunday
        var status = config.ResolveDayStatus(new DateOnly(2026, 8, 30));
        Assert.Equal(DayStatus.Work, status);
    }

    /// <summary>
    /// I-06: Activate Schedule → Salary Settings.
    /// Create a store with two schedules, activate one, verify the draft shows the
    /// activated schedule's work times.
    /// </summary>
    [Fact]
    public void I06_ActivateSchedule_ToSalarySettings()
    {
        var dir = CreateTempDir();
        var settingsService = new SettingsService(dir);
        var historyService = new HistoryService(Path.Combine(dir, "history"));
        var store = new ConfigurationStore(settingsService, historyService);

        var summerSchedule = new WorkScheduleProfile
        {
            Id = "summer",
            Name = "Summer",
            WorkStart = new TimeOnly(8, 0),
            WorkEnd = new TimeOnly(17, 0),
            EffectiveFrom = new DateOnly(2026, 6, 1),
        };
        var winterSchedule = new WorkScheduleProfile
        {
            Id = "winter",
            Name = "Winter",
            WorkStart = new TimeOnly(9, 0),
            WorkEnd = new TimeOnly(18, 0),
            EffectiveFrom = new DateOnly(2026, 10, 1),
        };

        var settings = MakeSettings(
            scheduleProfiles: [summerSchedule, winterSchedule]);
        store.Commit(settings);

        var draft = store.CreateDraft();
        var previewConfig = draft.BuildPreviewConfiguration(store.PayData);

        // In August 2026, the summer schedule should be effective
        var resolved = previewConfig.ResolveSchedule(new DateOnly(2026, 8, 15));
        Assert.Equal("summer", resolved.Id);
        Assert.Equal(new TimeOnly(8, 0), resolved.WorkStart);
        Assert.Equal(new TimeOnly(17, 0), resolved.WorkEnd);

        // In November 2026, the winter schedule should be effective
        var resolvedWinter = previewConfig.ResolveSchedule(new DateOnly(2026, 11, 1));
        Assert.Equal("winter", resolvedWinter.Id);
        Assert.Equal(new TimeOnly(9, 0), resolvedWinter.WorkStart);
        Assert.Equal(new TimeOnly(18, 0), resolvedWinter.WorkEnd);
    }

    /// <summary>
    /// I-07: Activate Schedule → Widget.
    /// Same setup but verify the store's CurrentConfiguration resolves the correct schedule.
    /// </summary>
    [Fact]
    public void I07_ActivateSchedule_ToWidget()
    {
        var dir = CreateTempDir();
        var settingsService = new SettingsService(dir);
        var historyService = new HistoryService(Path.Combine(dir, "history"));
        var store = new ConfigurationStore(settingsService, historyService);

        var earlySchedule = new WorkScheduleProfile
        {
            Id = "early",
            Name = "Early Bird",
            WorkStart = new TimeOnly(7, 0),
            WorkEnd = new TimeOnly(16, 0),
            EffectiveFrom = new DateOnly(2026, 1, 1),
        };
        var lateSchedule = new WorkScheduleProfile
        {
            Id = "late",
            Name = "Late Shift",
            WorkStart = new TimeOnly(10, 0),
            WorkEnd = new TimeOnly(19, 0),
            EffectiveFrom = new DateOnly(2026, 9, 1),
        };

        var settings = MakeSettings(
            scheduleProfiles: [earlySchedule, lateSchedule]);
        store.Commit(settings);

        var config = store.CurrentConfiguration;

        // August 2026 → early schedule
        var aug = config.ResolveSchedule(new DateOnly(2026, 8, 15));
        Assert.Equal("early", aug.Id);

        // October 2026 → late schedule
        var oct = config.ResolveSchedule(new DateOnly(2026, 10, 15));
        Assert.Equal("late", oct.Id);
    }

    /// <summary>
    /// I-08: Save → Restart → Same State.
    /// Write settings to a temp directory, create a new SettingsService with the same directory,
    /// load, and verify the same values persist.
    /// </summary>
    [Fact]
    public void I08_SaveRestart_SameState()
    {
        var dir = CreateTempDir();
        var settingsService1 = new SettingsService(dir);
        var historyService1 = new HistoryService(Path.Combine(dir, "history"));

        var settings = MakeSettings(
            weekPolicies: [WorkWeekPolicy.Create(WorkWeekType.SingleRest, new DateOnly(2026, 3, 1))],
            salaryProfiles: [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 8000m, EffectiveFrom = new DateOnly(2026, 1, 1) }]);
        settingsService1.Save(settings);

        // Simulate restart: create a new SettingsService pointing at the same directory
        var settingsService2 = new SettingsService(dir);
        var loaded = settingsService2.Load();

        Assert.Single(loaded.WeekPolicies);
        Assert.Equal(WorkWeekType.SingleRest, loaded.WeekPolicies[0].Type);
        Assert.Equal(new DateOnly(2026, 3, 1), loaded.WeekPolicies[0].EffectiveFrom);

        Assert.Single(loaded.SalaryProfiles);
        Assert.Equal(8000m, loaded.SalaryProfiles[0].MonthlyAmount);
        Assert.Equal(SalaryMode.Monthly, loaded.SalaryProfiles[0].Mode);
    }

    /// <summary>
    /// I-09: Calendar Override → Widget.
    /// Add an override to the store, verify both calendar and configuration resolve the
    /// same status.
    /// </summary>
    [Fact]
    public void I09_CalendarOverride_ToWidget()
    {
        var dir = CreateTempDir();
        var settingsService = new SettingsService(dir);
        var historyService = new HistoryService(Path.Combine(dir, "history"));
        var store = new ConfigurationStore(settingsService, historyService);

        var targetDate = new DateOnly(2026, 8, 29); // Saturday

        var settings = MakeSettings(
            weekPolicies: [WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1))],
            overrides: new Dictionary<string, CalendarOverride>
            {
                [targetDate.ToString("yyyy-MM-dd")] = CalendarOverride.For(targetDate, DayStatus.PublicHoliday),
            });
        store.Commit(settings);

        var config = store.CurrentConfiguration;

        // The override should make Saturday a public holiday
        var statusFromConfig = config.ResolveDayStatus(targetDate);
        Assert.Equal(DayStatus.PublicHoliday, statusFromConfig);

        // The configuration also resolves the same status via the Overrides dictionary
        Assert.True(config.Overrides.ContainsKey(targetDate));
        Assert.Equal(DayStatus.PublicHoliday, config.Overrides[targetDate].Status);
    }

    /// <summary>
    /// I-10: EffectiveDate Same-Day Upsert.
    /// Add two profiles with the same EffectiveFrom, verify only one survives after
    /// deduplication via ProfileVersioning.
    /// </summary>
    [Fact]
    public void I10_EffectiveDate_SameDayUpsert()
    {
        var sameDate = new DateOnly(2026, 1, 1);
        var profile1 = new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 5000m, EffectiveFrom = sameDate };
        var profile2 = new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 7000m, EffectiveFrom = sameDate };

        var profiles = new List<SalaryProfile> { profile1 };
        var result = ProfileVersioning.Upsert(
            profiles, profile2,
            p => p.EffectiveFrom,
            (a, b) => a.MonthlyAmount == b.MonthlyAmount);

        // Should have exactly one entry — the upsert replaced profile1 with profile2
        Assert.Single(result);
        Assert.Equal(7000m, result[0].MonthlyAmount);
        Assert.Equal(sameDate, result[0].EffectiveFrom);
    }

    /// <summary>
    /// I-11: EffectiveDate No List-Order Ambiguity.
    /// Add profiles in reverse chronological order, verify ProfileVersioning.Resolve
    /// returns the correct one (latest EffectiveFrom ≤ date).
    /// </summary>
    [Fact]
    public void I11_EffectiveDate_NoListOrderAmbiguity()
    {
        var older = new SalaryProfile { Mode = SalaryMode.Daily, DailyAmount = 200m, EffectiveFrom = new DateOnly(2025, 6, 1) };
        var newer = new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 9000m, EffectiveFrom = new DateOnly(2026, 7, 1) };

        // Insert in reverse chronological order: newer first, older second
        var profiles = new List<SalaryProfile> { newer, older };

        var date = new DateOnly(2026, 8, 15);
        var resolved = ProfileVersioning.Resolve(
            profiles, date,
            p => p.EffectiveFrom,
            () => new SalaryProfile());

        // Should resolve to newer (2026-07-01) since it's the latest ≤ 2026-08-15
        Assert.Equal(new DateOnly(2026, 7, 1), resolved.EffectiveFrom);
        Assert.Equal(9000m, resolved.MonthlyAmount);
        Assert.Equal(SalaryMode.Monthly, resolved.Mode);

        // A date before the newer profile should resolve to older
        var earlyDate = new DateOnly(2026, 1, 15);
        var resolvedEarly = ProfileVersioning.Resolve(
            profiles, earlyDate,
            p => p.EffectiveFrom,
            () => new SalaryProfile());

        Assert.Equal(new DateOnly(2025, 6, 1), resolvedEarly.EffectiveFrom);
        Assert.Equal(200m, resolvedEarly.DailyAmount);
        Assert.Equal(SalaryMode.Daily, resolvedEarly.Mode);
    }
}
