using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;

namespace PayBeat.Tests;

/// <summary>
/// Real end-to-end chains from the delivery gate, executed against the real service stack
/// (SettingsService → ConfigurationStore → ConfigurationDraft → CalendarViewModel /
/// MainViewModel source config → SalaryEngine). "Restart" is simulated by constructing a
/// fresh SettingsService + ConfigurationStore over the same on-disk directory.
/// </summary>
public class RealFlowTests : IDisposable
{
    private readonly string _tempDir;

    public RealFlowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PayBeatFlowTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ConfigurationStore CreateStore() => new(
        new SettingsService(_tempDir),
        new HistoryService(Path.Combine(_tempDir, "history")));

    private static DateOnly HolidayFreeSaturday()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        for (var i = 0; i < 40; i++)
        {
            if (date.DayOfWeek == DayOfWeek.Saturday && HolidayService.BuiltIn.Get(date) is null)
            {
                return date;
            }
            date = date.AddDays(1);
        }
        throw new InvalidOperationException("No holiday-free Saturday found within 40 days.");
    }

    private static DayStatus CalendarStatus(CalendarViewModel vm, DateOnly date)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var cell = vm.Days.FirstOrDefault(d => d.Date == date);
            if (cell is not null)
            {
                return cell.Status;
            }
            vm.NextMonth();
        }
        throw new InvalidOperationException($"Date {date} not found in calendar grid.");
    }

    private static List<WorkWeekPolicy> UpsertWeek(List<WorkWeekPolicy> policies, WorkWeekType type, DateOnly from)
    {
        var days = WorkWeekPolicy.Create(type, from).WorkDays;
        return ProfileVersioning.Upsert(policies, new WorkWeekPolicy { Type = type, WorkDays = days, EffectiveFrom = from },
            p => p.EffectiveFrom, (a, b) => a.Type == b.Type && a.WorkDays.SetEquals(b.WorkDays));
    }

    /// <summary>Same-day dedup must keep the LATEST submission (new same-day version replaces
    /// the existing one), never the accidental first-list-order entry.</summary>
    [Fact]
    public void DeduplicateByDate_SameDay_LastWriteWins()
    {
        var sameDay = new DateOnly(2026, 8, 29);
        var existing = new WorkScheduleProfile { Id = "old", Name = "默认作息", EffectiveFrom = sameDay };
        var submission = new WorkScheduleProfile { Id = "new", Name = "夏季作息", WorkStart = new TimeOnly(7, 30), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = sameDay };

        var deduped = ProfileVersioning.DeduplicateByDate([existing, submission], p => p.EffectiveFrom);

        var survivor = Assert.Single(deduped);
        Assert.Equal("夏季作息", survivor.Name);
        Assert.Equal(new TimeOnly(7, 30), survivor.WorkStart);
    }

    /// <summary>
    /// FLOW A: 月薪 6000 → 单休 → 保存 → 星期六 Calendar=工作、Widget=工作、Domain=工作。
    /// Widget evidence: the widget renders SalaryEngine.ComputeDayAt(store.CurrentConfiguration, …)
    /// each tick, so store.CurrentConfiguration IS the widget's configuration source.
    /// </summary>
    [Fact]
    public void FlowA_Monthly6000_SingleRest_SaturdayWorkEverywhere()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store); // widget view model (wired to store)
        var vm = new SettingsViewModel(store, mainVm);
        var calendar = new CalendarViewModel(store, mainVm, vm.Draft); // settings calendar page
        var saturday = HolidayFreeSaturday();
        var today = DateOnly.FromDateTime(DateTime.Now);

        // 月薪 6000 (default) + 单休 → effective-today policy in the draft → 保存
        Assert.Equal(6000m, vm.Draft.BuildPreviewConfiguration(store.PayData)
            .ResolveSalaryProfile(today).MonthlyAmount);
        vm.WeekType = WorkWeekType.SingleRest;
        vm.SaveCommand.Execute(null);

        // Calendar = 工作
        Assert.Equal(DayStatus.Work, CalendarStatus(calendar, saturday));

        // Widget = 工作 (the exact configuration instance MainViewModel renders from)
        var widgetConfig = store.CurrentConfiguration;
        Assert.Equal(DayStatus.Work, widgetConfig.ResolveDayStatus(saturday));

        // Domain = 工作
        Assert.Equal(DayStatus.Work, SalaryEngine.ComputeDay(widgetConfig, saturday).Status);

        // Widget view model actually re-rendered with the new config (rest-day banner off when it is today)
        if (saturday == today)
        {
            Assert.False(mainVm.IsRestDay);
        }

        // Persisted
        var reloaded = new SettingsService(_tempDir).Load();
        Assert.Equal(WorkWeekType.SingleRest,
            ProfileVersioning.Resolve(reloaded.WeekPolicies, today, p => p.EffectiveFrom, () => new WorkWeekPolicy()).Type);
    }

    /// <summary>FLOW B: 双休 → 保存 → 星期六 Calendar=休息、Widget=休息、Domain=休息。</summary>
    [Fact]
    public void FlowB_DoubleRest_SaturdayRestEverywhere()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var vm = new SettingsViewModel(store, mainVm);
        var calendar = new CalendarViewModel(store, mainVm, vm.Draft);
        var saturday = HolidayFreeSaturday();

        vm.WeekType = WorkWeekType.DoubleRest;
        vm.SaveCommand.Execute(null);

        Assert.Equal(DayStatus.Rest, CalendarStatus(calendar, saturday));
        Assert.Equal(DayStatus.Rest, store.CurrentConfiguration.ResolveDayStatus(saturday));
        Assert.Equal(DayStatus.Rest, SalaryEngine.ComputeDay(store.CurrentConfiguration, saturday).Status);

        var reloaded = new SettingsService(_tempDir).Load();
        Assert.Equal(WorkWeekType.DoubleRest,
            ProfileVersioning.Resolve(reloaded.WeekPolicies, DateOnly.FromDateTime(DateTime.Now), p => p.EffectiveFrom, () => new WorkWeekPolicy()).Type);
    }

    /// <summary>
    /// FLOW C: 建立夏季作息 07:30-17:00 → 设为当前 → 薪资页=07:30-17:00 → Widget 同方案
    /// → 保存 → 重启 → 仍然一致。
    /// </summary>
    [Fact]
    public void FlowC_SummerSchedule_Activate_Save_Restart_Consistent()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        // 建立夏季作息 (schedule manager writes into the shared draft)
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var vm = new SettingsViewModel(store, mainVm);
        var draft = vm.Draft;

        draft.ScheduleProfiles = ProfileVersioning.Upsert(
            draft.ScheduleProfiles,
            new WorkScheduleProfile { Name = "🌞 夏季作息", WorkStart = new TimeOnly(7, 30), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = today },
            p => p.EffectiveFrom, (a, b) => a.Id == b.Id);

        // 设为当前 = effective-today upsert already IS "current" — salary page refreshes from draft
        vm.RefreshFromDraft();
        Assert.Equal(new TimeOnly(7, 30), vm.WorkStart);   // 薪资页 = 07:30
        Assert.Equal(new TimeOnly(17, 0), vm.WorkEnd);     //          -17:00

        // Widget uses the same schedule (through draft preview before save)
        Assert.Equal(new TimeOnly(7, 30),
            draft.BuildPreviewConfiguration(store.PayData).ResolveSchedule(today).WorkStart);

        // 保存
        vm.SaveCommand.Execute(null);
        Assert.Equal(new TimeOnly(7, 30),
            store.CurrentConfiguration.ResolveSchedule(today).WorkStart);
        Assert.Equal(new TimeOnly(7, 30), mainVm.WorkStart); // live widget refreshed

        // 重启: fresh SettingsService + Store over the same directory
        var restartedStore = new ConfigurationStore(
            new SettingsService(_tempDir),
            new HistoryService(Path.Combine(_tempDir, "history")));
        var restartedMainVm = new MainViewModel(restartedStore);
        Assert.Equal(new TimeOnly(7, 30),
            restartedStore.CurrentConfiguration.ResolveSchedule(today).WorkStart);
        Assert.Equal(new TimeOnly(7, 30), restartedMainVm.WorkStart);
        Assert.Equal("🌞 夏季作息", restartedStore.CurrentConfiguration.ResolveSchedule(today).Name);
    }

    /// <summary>FLOW D: 切换冬季作息 → Settings / Calendar(Widget 源) / SalaryEngine 一致。</summary>
    [Fact]
    public void FlowD_WinterSchedule_AllSurfacesConsistent()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var vm = new SettingsViewModel(store, mainVm);
        var draft = vm.Draft;

        // 先切夏季（沿用 C 的结果作为前置状态）
        draft.ScheduleProfiles = ProfileVersioning.Upsert(
            draft.ScheduleProfiles,
            new WorkScheduleProfile { Name = "🌞 夏季作息", WorkStart = new TimeOnly(7, 30), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = today },
            p => p.EffectiveFrom, (a, b) => a.Id == b.Id);
        vm.SaveCommand.Execute(null);

        // 切换冬季作息 08:00-17:00，午休 12:00-13:00
        draft.ScheduleProfiles = ProfileVersioning.Upsert(
            draft.ScheduleProfiles,
            new WorkScheduleProfile { Name = "❄️ 冬季作息", WorkStart = new TimeOnly(8, 0), WorkEnd = new TimeOnly(17, 0), LunchBreakEnabled = true, LunchBreakStart = new TimeOnly(12, 0), LunchBreakEnd = new TimeOnly(13, 0), EffectiveFrom = today },
            p => p.EffectiveFrom, (a, b) => a.Id == b.Id);
        vm.RefreshFromDraft();

        // Settings 薪资页
        Assert.Equal(new TimeOnly(8, 0), vm.WorkStart);
        Assert.Equal(new TimeOnly(17, 0), vm.WorkEnd);
        Assert.True(vm.LunchBreakEnabled);

        // Widget 源配置（MainViewModel 每 tick 渲染的同一实例）
        var widgetSchedule = store.CurrentConfiguration.ResolveSchedule(today);
        // commit happened through vm.Save below — first verify via draft preview
        var previewSchedule = draft.BuildPreviewConfiguration(store.PayData).ResolveSchedule(today);
        Assert.Equal(new TimeOnly(8, 0), previewSchedule.WorkStart);

        // 保存后 Widget 源
        vm.SaveCommand.Execute(null);
        widgetSchedule = store.CurrentConfiguration.ResolveSchedule(today);
        Assert.Equal(new TimeOnly(8, 0), widgetSchedule.WorkStart);
        Assert.Equal(new TimeOnly(17, 0), widgetSchedule.WorkEnd);
        Assert.Equal(new TimeOnly(8, 0), mainVm.WorkStart); // widget live

        // SalaryEngine 与 Widget/Settings 同源
        var engineSchedule = SalaryEngine.ComputeDay(store.CurrentConfiguration, today).Schedule;
        Assert.Equal(widgetSchedule, engineSchedule);

        // 历史不受污染：今天之前的日期仍解析到旧版本（2000-01-01 默认 09:00）
        var before = today.AddDays(-1);
        var historySchedule = store.CurrentConfiguration.ResolveSchedule(before);
        Assert.Equal(new TimeOnly(9, 0), historySchedule.WorkStart);
    }
}
