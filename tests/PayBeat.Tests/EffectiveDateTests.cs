using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;

namespace PayBeat.Tests;

/// <summary>
/// Effective-date semantics: salary amount and work policy default to the FIRST DAY OF THE
/// CURRENT MONTH (today and custom dates are explicit user choices), schedules keep their
/// "today" default, history before the effective date is never rewritten, and same-month
/// edits upsert deterministically.
/// </summary>
public class EffectiveDateTests : IDisposable
{
    private readonly string _tempDir;

    public EffectiveDateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PayBeatEff_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ConfigurationStore CreateStore() => new(
        new SettingsService(_tempDir),
        new HistoryService(Path.Combine(_tempDir, "history")));

    private DateOnly FirstOfMonth => new(DateTime.Now.Year, DateTime.Now.Month, 1);
    private DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>A salary profile whose EffectiveFrom covers the month of <paramref name="date"/>.</summary>
    private static SalaryProfile MonthlyAt(List<SalaryProfile> profiles, DateOnly date) =>
        ProfileVersioning.Resolve(profiles, date, p => p.EffectiveFrom, () => new SalaryProfile());

    private static WorkWeekPolicy WeekAt(List<WorkWeekPolicy> policies, DateOnly date) =>
        ProfileVersioning.Resolve(policies, date, p => p.EffectiveFrom, () => new WorkWeekPolicy());

    /// <summary>Saves through the real SettingsViewModel pipeline with the given amount/week/choice.</summary>
    private (ConfigurationStore Store, MainViewModel Main, SettingsViewModel Vm) SaveWith(
        decimal amount, WorkWeekType week, SettingsViewModel.EffectiveDateChoice choice, string? customDate = null)
    {
        var store = CreateStore();
        var main = new MainViewModel(store);
        var vm = new SettingsViewModel(store, main);
        vm.AmountText = amount.ToString("G29");
        vm.WeekType = week;
        vm.Choice = choice;
        if (customDate is not null)
        {
            vm.CustomEffectiveDateText = customDate;
        }
        vm.SaveCommand.Execute(null);
        return (store, main, vm);
    }

    // 1 + 2. First setup defaults to first day of month -----------------------------------

    [Fact]
    public void FirstSetup_SalaryDefaultsToFirstDayOfMonth()
    {
        var (store, _, _) = SaveWith(6000m, WorkWeekType.DoubleRest, SettingsViewModel.EffectiveDateChoice.FirstOfMonth);

        var profile = MonthlyAt(store.CurrentSettings.SalaryProfiles, Today);
        Assert.Equal(6000m, profile.MonthlyAmount);
        Assert.Equal(FirstOfMonth, profile.EffectiveFrom);
    }

    [Fact]
    public void FirstSetup_WorkWeekDefaultsToFirstDayOfMonth()
    {
        var (store, _, _) = SaveWith(6000m, WorkWeekType.SingleRest, SettingsViewModel.EffectiveDateChoice.FirstOfMonth);

        var policy = WeekAt(store.CurrentSettings.WeekPolicies, Today);
        Assert.Equal(WorkWeekType.SingleRest, policy.Type);
        Assert.Equal(FirstOfMonth, policy.EffectiveFrom);
    }

    // 3 + 4. Explicit "today" choice -------------------------------------------------------

    [Fact]
    public void SalaryTodayOption_UsesToday()
    {
        var (store, _, _) = SaveWith(7000m, WorkWeekType.DoubleRest, SettingsViewModel.EffectiveDateChoice.Today);
        var profile = MonthlyAt(store.CurrentSettings.SalaryProfiles, Today);
        Assert.Equal(7000m, profile.MonthlyAmount);
        Assert.Equal(Today, profile.EffectiveFrom);
    }

    [Fact]
    public void WorkWeekTodayOption_UsesToday()
    {
        var (store, _, _) = SaveWith(6000m, WorkWeekType.SingleRest, SettingsViewModel.EffectiveDateChoice.Today);
        var policy = WeekAt(store.CurrentSettings.WeekPolicies, Today);
        Assert.Equal(WorkWeekType.SingleRest, policy.Type);
        Assert.Equal(Today, policy.EffectiveFrom);
    }

    // 5 + 6. Custom effective dates ---------------------------------------------------------

    [Fact]
    public void SalaryCustomEffectiveDate()
    {
        var custom = Today.AddDays(17); // a specific user-chosen date
        var (store, _, _) = SaveWith(6500m, WorkWeekType.DoubleRest,
            SettingsViewModel.EffectiveDateChoice.Custom, custom.ToString("yyyy-MM-dd"));

        var profile = MonthlyAt(store.CurrentSettings.SalaryProfiles, custom);
        Assert.Equal(6500m, profile.MonthlyAmount);
        Assert.Equal(custom, profile.EffectiveFrom);

        // Before the custom date the previous amount still applies (no rewrite of earlier days)
        Assert.Equal(6000m, MonthlyAt(store.CurrentSettings.SalaryProfiles, FirstOfMonth).MonthlyAmount);
    }

    [Fact]
    public void WorkWeekCustomEffectiveDate()
    {
        var custom = Today.AddDays(3);
        var (store, _, _) = SaveWith(6000m, WorkWeekType.DoubleRest,
            SettingsViewModel.EffectiveDateChoice.Custom, custom.ToString("yyyy-MM-dd"));

        var policy = WeekAt(store.CurrentSettings.WeekPolicies, custom);
        Assert.Equal(WorkWeekType.DoubleRest, policy.Type);
        Assert.Equal(custom, policy.EffectiveFrom);
        // Before the custom date the previous (default) policy still applies
        Assert.NotEqual(custom, WeekAt(store.CurrentSettings.WeekPolicies, custom.AddDays(-1)).EffectiveFrom);
    }

    // 7. Same-month first-day upsert --------------------------------------------------------

    [Fact]
    public void SameMonthFirstDay_UpertsInsteadOfDuplicating()
    {
        var (store, main, _) = SaveWith(6000m, WorkWeekType.DoubleRest, SettingsViewModel.EffectiveDateChoice.FirstOfMonth);
        var firstCount = store.CurrentSettings.SalaryProfiles.Count(p => p.EffectiveFrom == FirstOfMonth);
        Assert.Equal(1, firstCount);

        // Second save in the same month with 本月1日起 → replace, not duplicate
        var vm2 = new SettingsViewModel(store, main);
        vm2.AmountText = "6500";
        vm2.Choice = SettingsViewModel.EffectiveDateChoice.FirstOfMonth;
        vm2.SaveCommand.Execute(null);

        var settings = store.CurrentSettings;
        Assert.Equal(1, settings.SalaryProfiles.Count(p => p.EffectiveFrom == FirstOfMonth));
        Assert.Equal(6500m, MonthlyAt(settings.SalaryProfiles, Today).MonthlyAmount);
        Assert.Equal(1, settings.WeekPolicies.Count(p => p.EffectiveFrom == FirstOfMonth));
    }

    // 8. Previous month unaffected ----------------------------------------------------------

    [Fact]
    public void FirstOfMonthChoice_DoesNotTouchEarlierMonths()
    {
        var (store, _, _) = SaveWith(6000m, WorkWeekType.DoubleRest, SettingsViewModel.EffectiveDateChoice.FirstOfMonth);

        // The seeded 2000-01-01 default (5000 profile semantics) still resolves for earlier months;
        // July 2026 must not be claimed by the new August profile.
        var july = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1).AddDays(14);
        var julyProfile = MonthlyAt(store.CurrentSettings.SalaryProfiles, july);
        Assert.NotEqual(FirstOfMonth, julyProfile.EffectiveFrom);
        Assert.True(julyProfile.EffectiveFrom < FirstOfMonth);
    }

    // 9. Single rest covers the whole current month -----------------------------------------

    [Fact]
    public void SingleRestFromFirstOfMonth_WholeMonthSaturdaysWork()
    {
        var store = CreateStore();
        var main = new MainViewModel(store);
        var vm = new SettingsViewModel(store, main);
        vm.AmountText = "6000";
        vm.WeekType = WorkWeekType.SingleRest;
        vm.Choice = SettingsViewModel.EffectiveDateChoice.FirstOfMonth;
        vm.SaveCommand.Execute(null);

        var config = store.CurrentConfiguration;
        var month = FirstOfMonth;
        var saturdays = new List<DateOnly>();
        var sundays = new List<DateOnly>();
        foreach (var date in EachDayOf(month))
        {
            if (date.DayOfWeek == DayOfWeek.Saturday) saturdays.Add(date);
            if (date.DayOfWeek == DayOfWeek.Sunday) sundays.Add(date);
        }

        // 2026-08 has no statutory holidays; general months may shift individual days via the
        // official dataset, so assert via the domain rule the product promises: weekend days
        // without holiday overrides resolve from the week policy.
        foreach (var sat in saturdays)
        {
            if (config.Holidays.Get(sat) is null)
            {
                Assert.Equal(DayStatus.Work, config.ResolveDayStatus(sat));
            }
        }
        foreach (var sun in sundays)
        {
            if (config.Holidays.Get(sun) is null)
            {
                Assert.Equal(DayStatus.Rest, config.ResolveDayStatus(sun));
            }
        }

        // Engine agrees with the calendar for every holiday-free Saturday
        foreach (var sat in saturdays.Where(s => config.Holidays.Get(s) is null))
        {
            Assert.Equal(DayStatus.Work, SalaryEngine.ComputeDay(config, sat).Status);
        }
    }

    // 10. Standard daily rate uses the whole-month workweek ---------------------------------

    [Fact]
    public void StandardDailyRate_UsesWholeMonthWorkdays()
    {
        var store = CreateStore();
        var main = new MainViewModel(store);
        var vm = new SettingsViewModel(store, main);
        vm.AmountText = "6000";
        vm.WeekType = WorkWeekType.SingleRest;
        vm.Choice = SettingsViewModel.EffectiveDateChoice.FirstOfMonth;
        vm.SaveCommand.Execute(null);

        var config = store.CurrentConfiguration;
        var midMonth = new DateOnly(Today.Year, Today.Month, Math.Min(15, DateTime.DaysInMonth(Today.Year, Today.Month)));
        var planned = config.PlannedWorkdays(new DateOnly(Today.Year, Today.Month, 1));
        Assert.True(planned > 0);

        // The rate must be amount / whole-month planned workdays — independent of when in
        // the month the setting was made.
        TestConfig.AssertMoney(6000m / planned, config.StandardDailyRate(midMonth));

        // For 2026-08 specifically (no statutory holidays): single rest = 26 planned days.
        if (Today.Year == 2026 && Today.Month == 8)
        {
            Assert.Equal(26, planned);
            TestConfig.AssertMoney(6000m / 26m, config.StandardDailyRate(midMonth));
        }
    }

    // 11. Existing overrides remain untouched ------------------------------------------------

    [Fact]
    public void Save_PreservesExistingOverrides()
    {
        var store = CreateStore();
        var main = new MainViewModel(store);
        var saturday = NextSaturday();

        var vm = new SettingsViewModel(store, main);
        vm.Draft.Overrides = new Dictionary<string, CalendarOverride>(vm.Draft.Overrides)
        {
            [saturday.ToString("yyyy-MM-dd")] = CalendarOverride.For(saturday, DayStatus.Work),
        };
        vm.AmountText = "6000";
        vm.Choice = SettingsViewModel.EffectiveDateChoice.FirstOfMonth;
        vm.SaveCommand.Execute(null);

        var settings = store.CurrentSettings;
        Assert.True(settings.Overrides.ContainsKey(saturday.ToString("yyyy-MM-dd")),
            "manual overrides must survive a salary/work-policy save");
    }

    // 12. Restore auto after rule change → WeekPolicy source ---------------------------------

    [Fact]
    public void RestoreAuto_AfterFirstOfMonthRule_ResolvesToWorkPolicySource()
    {
        var store = CreateStore();
        var main = new MainViewModel(store);
        var vm = new SettingsViewModel(store, main);
        vm.AmountText = "6000";
        vm.WeekType = WorkWeekType.SingleRest;
        vm.Choice = SettingsViewModel.EffectiveDateChoice.FirstOfMonth;
        vm.SaveCommand.Execute(null);

        var config = store.CurrentConfiguration;
        var saturday = NextHolidayFreeSaturday(config);

        // Legacy manual override says Rest (user's old workaround)
        var overrides = new Dictionary<string, CalendarOverride>(store.CurrentSettings.Overrides)
        {
            [saturday.ToString("yyyy-MM-dd")] = CalendarOverride.For(saturday, DayStatus.Rest),
        };
        store.Commit(store.CurrentSettings with { Overrides = overrides });
        config = store.CurrentConfiguration;

        Assert.Equal(DayStatus.Rest, config.ResolveDayStatus(saturday));
        Assert.Equal(DayStatusSource.ManualOverride, config.ResolveDayStatusSource(saturday));

        // 恢复自动判断: remove the override → base rule (single rest from the 1st) says Work
        overrides.Remove(saturday.ToString("yyyy-MM-dd"));
        var restored = store.CurrentSettings with { Overrides = overrides };
        store.Commit(restored);

        config = store.CurrentConfiguration;
        Assert.Equal(DayStatus.Work, config.ResolveDayStatus(saturday));
        Assert.Equal(DayStatusSource.WeekPolicy, config.ResolveDayStatusSource(saturday));
        Assert.Equal(DayStatus.Work, SalaryEngine.ComputeDay(config, saturday).Status);
    }

    private DateOnly NextSaturday()
    {
        var date = Today;
        while (date.DayOfWeek != DayOfWeek.Saturday) date = date.AddDays(1);
        return date;
    }

    private static IEnumerable<DateOnly> EachDayOf(DateOnly month)
    {
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        for (var d = 1; d <= days; d++)
        {
            yield return new DateOnly(month.Year, month.Month, d);
        }
    }

    private static DateOnly NextHolidayFreeSaturday(PayConfiguration config)
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

// ── Regression: "本月1日起" must supersede later-dated versions (user bug 2026-08-30) ──

/// <summary>
/// User had saved 单休 effective 2026-08-29; on 08-30 they switched to 双休 with the default
/// 本月1日起. The new 08-01 version must NOT be outranked by the later-dated 单休@08-29 when
/// resolving today — the chosen date supersedes everything from that date on.
/// </summary>
[Fact]
public void FirstOfMonthSave_SupersedesLaterDatedVersions()
{
    var store = CreateStore();
    var main = new MainViewModel(store);
    var today = Today;

    // Seed the user's real history: 单休 saved yesterday (08-29), 双休 seed at 2000-01-01
    var seeded = store.CurrentSettings with
    {
        WeekPolicies =
        [
            WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1)),
            WorkWeekPolicy.Create(WorkWeekType.SingleRest, today.AddDays(-1)),
        ],
    };
    store.Commit(seeded);

    Assert.Equal(WorkWeekType.SingleRest, WeekAt(store.CurrentSettings.WeekPolicies, today).Type);

    // Switch to 双休 with the default 本月1日起
    var vm = new SettingsViewModel(store, main);
    vm.WeekType = WorkWeekType.DoubleRest;
    vm.SaveCommand.Execute(null);

    var policies = store.CurrentSettings.WeekPolicies;
    // No version at/after 08-01 other than the new one may survive
    Assert.DoesNotContain(policies, p => p.EffectiveFrom >= FirstOfMonth && p.Type == WorkWeekType.SingleRest);
    Assert.Equal(WorkWeekType.DoubleRest, WeekAt(policies, today).Type);

    // Whole current month resolves from the new rule (holiday-free days only):
    // Mon–Fri work, Sat+Sun rest — including today, whatever weekday it is.
    var config = store.CurrentConfiguration;
    foreach (var date in EachDayOf(FirstOfMonth))
    {
        if (config.Holidays.Get(date) is null)
        {
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            Assert.Equal(weekend ? DayStatus.Rest : DayStatus.Work, config.ResolveDayStatus(date));
        }
    }
    Assert.Equal(
        Today.DayOfWeek == DayOfWeek.Sunday ? DayStatus.Rest : DayStatus.Work,
        config.ResolveDayStatus(Today));
}

/// <summary>Calendar preview follows the same supersede semantics as the save path.</summary>
[Fact]
public void DraftPreview_SupersedesLaterDatedVersions()
{
    var store = CreateStore();
    var main = new MainViewModel(store);
    var today = Today;

    store.Commit(store.CurrentSettings with
    {
        WeekPolicies =
        [
            WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1)),
            WorkWeekPolicy.Create(WorkWeekType.SingleRest, today.AddDays(-1)),
        ],
    });

    var vm = new SettingsViewModel(store, main);
    vm.WeekType = WorkWeekType.DoubleRest; // chip click → ApplyWeekToDraft (unsaved preview)

    var preview = vm.Draft.BuildPreviewConfiguration(store.PayData);
    Assert.Equal(WorkWeekType.DoubleRest, WeekAt(vm.Draft.WeekPolicies, today).Type);
    // Today resolves through the NEW rule (double rest), not the old 单休 version:
    Assert.Equal(WorkWeekType.DoubleRest, preview.ResolveWeekPolicy(today).Type);
    Assert.Equal(
        Today.DayOfWeek == DayOfWeek.Sunday ? DayStatus.Rest : DayStatus.Work,
        preview.ResolveDayStatus(today));
}

/// <summary>Future-dated versions are also superseded when the user picks an earlier date.</summary>
[Fact]
public void EarlierChosenDate_SupersedesFutureVersion()
{
    var store = CreateStore();
    var main = new MainViewModel(store);
    var vm = new SettingsViewModel(store, main);

    // Seed a future raise (09-15 @ 6500), then re-save this month at 6000 from the 1st
    vm.AmountText = "6500";
    vm.Choice = SettingsViewModel.EffectiveDateChoice.Custom;
    vm.CustomEffectiveDateText = new DateOnly(Today.Year, 9, 15).ToString("yyyy-MM-dd");
    vm.SaveCommand.Execute(null);

    var vm2 = new SettingsViewModel(store, main);
    vm2.AmountText = "6000";
    vm2.Choice = SettingsViewModel.EffectiveDateChoice.FirstOfMonth;
    vm2.SaveCommand.Execute(null);

    var profiles = store.CurrentSettings.SalaryProfiles;
    Assert.DoesNotContain(profiles, p => p.EffectiveFrom >= FirstOfMonth && p.MonthlyAmount == 6500m);
    Assert.Equal(6000m, MonthlyAt(profiles, Today).MonthlyAmount);
}
}
