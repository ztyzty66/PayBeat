using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;

namespace PayBeat.Tests;

/// <summary>
/// Regression tests for the V1 human-acceptance round: schedule-list presentation (no type-name
/// leak), day-status source transparency, restore-auto semantics, and override preservation
/// across work-policy changes.
/// </summary>
public class RegressionTests : IDisposable
{
    private readonly string _tempDir;

    public RegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PayBeatRegr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ConfigurationStore CreateStore() => new(
        new SettingsService(_tempDir),
        new HistoryService(Path.Combine(_tempDir, "history")));

    private static DateOnly Saturday(DateOnly weekOf)
    {
        var d = weekOf;
        while (d.DayOfWeek != DayOfWeek.Saturday)
        {
            d = d.AddDays(1);
        }
        return d;
    }

    // 1. Schedule list presentation never falls back to Object.ToString -------------------

    [Fact]
    public void ScheduleRowVm_DisplayNeverLeaksTypeName()
    {
        var schedule = new WorkScheduleProfile
        {
            Name = "🌞 夏季作息",
            WorkStart = new TimeOnly(7, 30),
            WorkEnd = new TimeOnly(17, 0),
            LunchBreakEnabled = true,
            LunchBreakStart = new TimeOnly(11, 15),
            LunchBreakEnd = new TimeOnly(12, 45),
            EffectiveFrom = new DateOnly(2026, 5, 1),
        };

        var row = new ScheduleRowVm(schedule, isActive: true);

        Assert.Equal("🌞 夏季作息", row.Name);
        Assert.Equal("07:30 – 17:00", row.TimeText);
        Assert.True(row.HasLunch);
        Assert.Equal("11:15 – 12:45", row.LunchText);
        Assert.Equal("2026-05-01", row.EffectiveDateText);
        Assert.True(row.IsActive);
        Assert.NotEqual(string.Empty, row.ActiveText);

        // The fatal leak: ToString() (what a template-less ListBox would render) must never
        // expose namespace/type names.
        var text = row.ToString();
        Assert.DoesNotContain("PayBeat.App", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ScheduleRow", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ViewModels", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("🌞 夏季作息", text);
    }

    [Fact]
    public void ScheduleRowVm_NoLunch_ShowsDash_NotZeros()
    {
        var row = new ScheduleRowVm(new WorkScheduleProfile
        {
            Name = "❄️ 冬季作息",
            WorkStart = new TimeOnly(8, 0),
            WorkEnd = new TimeOnly(17, 0),
            LunchBreakEnabled = false,
            EffectiveFrom = new DateOnly(2026, 10, 1),
        }, isActive: false);

        Assert.False(row.HasLunch);
        Assert.Equal("—", row.LunchText);
        Assert.False(row.IsActive);
        Assert.Equal(string.Empty, row.ActiveText);
    }

    // 2. New schedule draft does not commit on cancel -------------------------------------

    [Fact]
    public void NewScheduleDraft_CancelDoesNotCommit()
    {
        var store = CreateStore();
        var draft = store.CreateDraft();

        draft.ScheduleProfiles = ProfileVersioning.Upsert(
            draft.ScheduleProfiles,
            new WorkScheduleProfile { Name = "未保存方案", WorkStart = new TimeOnly(6, 0), WorkEnd = new TimeOnly(15, 0), EffectiveFrom = DateOnly.FromDateTime(DateTime.Now) },
            p => p.EffectiveFrom, (a, b) => a.Id == b.Id);

        // Simulating cancel = simply discarding the draft: store, disk and revision untouched.
        Assert.False(File.Exists(Path.Combine(_tempDir, "settings.json")));
        Assert.Equal(0, store.Revision);
        Assert.DoesNotContain(store.CurrentSettings.ScheduleProfiles, s => s.Name == "未保存方案");
        Assert.NotEqual("未保存方案", store.CurrentConfiguration.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now)).Name);
    }

    // 3. New schedule commit persists after save/restart ----------------------------------

    [Fact]
    public void NewSchedule_CommitPersistsAfterRestart()
    {
        var store = CreateStore();
        var today = DateOnly.FromDateTime(DateTime.Now);

        var draft = store.CreateDraft();
        draft.ScheduleProfiles = ProfileVersioning.Upsert(
            draft.ScheduleProfiles,
            new WorkScheduleProfile { Name = "🌞 夏季作息", WorkStart = new TimeOnly(7, 30), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = today },
            p => p.EffectiveFrom, (a, b) => a.Id == b.Id);
        store.Commit(draft.ToSettings());

        // Restart: fresh services over the same directory
        var reloaded = new SettingsService(_tempDir).Load();
        var resolved = ProfileVersioning.Resolve(reloaded.ScheduleProfiles, today, p => p.EffectiveFrom, () => new WorkScheduleProfile());
        Assert.Equal("🌞 夏季作息", resolved.Name);
        Assert.Equal(new TimeOnly(7, 30), resolved.WorkStart);
    }

    // 4-7. Day-status source transparency + restore auto ----------------------------------

    private static PayConfiguration ConfigWith(
        List<WorkWeekPolicy> policies,
        Dictionary<string, CalendarOverride>? overrides = null)
    {
        var byDate = new Dictionary<DateOnly, CalendarOverride>();
        foreach (var (key, value) in overrides ?? [])
        {
            if (DateOnly.TryParseExact(key, "yyyy-MM-dd", out var d))
            {
                byDate[d] = value with { Date = d };
            }
        }
        return new PayConfiguration
        {
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 6000m }],
            ScheduleProfiles = [new WorkScheduleProfile { EffectiveFrom = new DateOnly(2000, 1, 1) }],
            WeekPolicies = policies,
            Overrides = byDate,
            Holidays = HolidayService.BuiltIn,
        };
    }

    [Fact]
    public void OverrideStatus_SourceReportsManualOverride()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var single = WorkWeekPolicy.Create(WorkWeekType.SingleRest, new DateOnly(2000, 1, 1));
        var config = ConfigWith([single], new Dictionary<string, CalendarOverride>
        {
            [today.ToString("yyyy-MM-dd")] = CalendarOverride.For(today, DayStatus.Rest),
        });

        Assert.Equal(DayStatus.Rest, config.ResolveDayStatus(today));
        Assert.Equal(DayStatusSource.ManualOverride, config.ResolveDayStatusSource(today));
    }

    [Fact]
    public void NonOverrideDay_SourceReportsWeekPolicy()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var config = ConfigWith([WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1))]);
        var src = config.ResolveDayStatusSource(today);
        Assert.True(src is DayStatusSource.WeekPolicy or DayStatusSource.DefaultRule);
    }

    [Fact]
    public void RestoreAuto_RemovesOverride_AndRecomputesBaseWorkWeek()
    {
        var saturday = Saturday(new DateOnly(2026, 8, 1)); // any holiday-free-ish Saturday; source check still valid
        var single = WorkWeekPolicy.Create(WorkWeekType.SingleRest, new DateOnly(2000, 1, 1)); // Saturday works

        var config = ConfigWith([single], new Dictionary<string, CalendarOverride>
        {
            [saturday.ToString("yyyy-MM-dd")] = CalendarOverride.For(saturday, DayStatus.Rest),
        });

        Assert.Equal(DayStatus.Rest, config.ResolveDayStatus(saturday));
        Assert.Equal(DayStatusSource.ManualOverride, config.ResolveDayStatusSource(saturday));

        // "恢复自动判断" = delete the override, re-resolve
        var withoutOverride = config with { Overrides = new Dictionary<DateOnly, CalendarOverride>() };
        Assert.Equal(DayStatus.Work, withoutOverride.ResolveDayStatus(saturday));
        Assert.NotEqual(DayStatusSource.ManualOverride, withoutOverride.ResolveDayStatusSource(saturday));
    }

    [Fact]
    public void RestoreAuto_SingleRestSaturday_IsWork_CalendarAndWidgetAgree()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store); // widget source
        var today = DateOnly.FromDateTime(DateTime.Now);

        var draft = store.CreateDraft();
        var saturday = draft.BuildPreviewConfiguration(store.PayData) is { } c
            ? NextSaturday(c)
            : throw new InvalidOperationException("no config");

        // Old manual override said Rest (user confusion scenario from the acceptance report)
        draft.Overrides = new Dictionary<string, CalendarOverride>(draft.Overrides)
        {
            [saturday.ToString("yyyy-MM-dd")] = CalendarOverride.For(saturday, DayStatus.Rest),
        };
        // Single rest policy effective today
        draft.WeekPolicies = ProfileVersioning.Upsert(
            draft.WeekPolicies,
            WorkWeekPolicy.Create(WorkWeekType.SingleRest, today),
            p => p.EffectiveFrom, (a, b) => a.Type == b.Type && a.WorkDays.SetEquals(b.WorkDays));

        var withOverride = draft.BuildPreviewConfiguration(store.PayData);
        Assert.Equal(DayStatus.Rest, withOverride.ResolveDayStatus(saturday)); // manual wins

        // Restore auto: delete override from the draft
        var overrides = new Dictionary<string, CalendarOverride>(draft.Overrides);
        overrides.Remove(saturday.ToString("yyyy-MM-dd"));
        draft.Overrides = overrides;
        store.Commit(draft.ToSettings());

        // Calendar cell source and widget source are the same committed configuration
        var widgetConfig = store.CurrentConfiguration;
        Assert.Equal(DayStatus.Work, widgetConfig.ResolveDayStatus(saturday));
        Assert.Equal(DayStatusSource.WeekPolicy, widgetConfig.ResolveDayStatusSource(saturday));
        Assert.Equal(widgetConfig.ResolveDayStatus(saturday), SalaryEngine.ComputeDay(widgetConfig, saturday).Status);

        // Disk state matches (persisted once)
        var reloaded = new SettingsService(_tempDir).Load();
        Assert.False(reloaded.Overrides.ContainsKey(saturday.ToString("yyyy-MM-dd")));
    }

    // 10. Override remains when workweek changes unless the user restores auto -------------

    [Fact]
    public void WorkPolicyChange_PreservesExistingOverrides()
    {
        var store = CreateStore();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var draft = store.CreateDraft();
        var saturday = NextSaturday(draft.BuildPreviewConfiguration(store.PayData));

        draft.Overrides = new Dictionary<string, CalendarOverride>(draft.Overrides)
        {
            [saturday.ToString("yyyy-MM-dd")] = CalendarOverride.For(saturday, DayStatus.Work),
        };

        // Switch work policy double → single: the (now redundant) override must survive.
        draft.WeekPolicies = ProfileVersioning.Upsert(
            draft.WeekPolicies,
            WorkWeekPolicy.Create(WorkWeekType.SingleRest, today),
            p => p.EffectiveFrom, (a, b) => a.Type == b.Type && a.WorkDays.SetEquals(b.WorkDays));

        var config = draft.BuildPreviewConfiguration(store.PayData);
        Assert.True(config.Overrides.ContainsKey(saturday), "override must be preserved on policy change");
        Assert.Equal(DayStatusSource.ManualOverride, config.ResolveDayStatusSource(saturday));
        Assert.Equal(DayStatus.Work, config.ResolveDayStatus(saturday)); // same result, but still manual
    }

    private static DateOnly NextSaturday(PayConfiguration config)
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        for (var i = 0; i < 40; i++)
        {
            if (date.DayOfWeek == DayOfWeek.Saturday && config.Holidays.Get(date) is null)
            {
                return date;
            }
            date = date.AddDays(1);
        }
        throw new InvalidOperationException("No holiday-free Saturday within 40 days.");
    }
}
