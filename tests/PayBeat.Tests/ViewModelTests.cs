using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;

namespace PayBeat.Tests;

/// <summary>
/// ViewModel-level tests for state propagation through the ConfigurationStore / ConfigurationDraft.
/// WPF dependency is isolated rather than abandoned: services are null-safe without an
/// Application instance, and DispatcherTimer simply never ticks in tests (no pump needed —
/// we trigger Refresh paths explicitly via store commits).
/// </summary>
public class ViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public ViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PayBeatVmTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        HotkeyService.LastRegistrationSucceeded = null;
    }

    public void Dispose()
    {
        HotkeyService.LastRegistrationSucceeded = null;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ConfigurationStore CreateStore() => new(
        new SettingsService(_tempDir),
        new HistoryService(Path.Combine(_tempDir, "history")));

    private static string SettingsFilePath(string dir) => Path.Combine(dir, "settings.json");

    /// <summary>First Saturday from today (within 5 weeks) that has no official holiday entry,
    /// so week-policy expectations are not overridden by the built-in holiday dataset.</summary>
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

    /// <summary>Status the calendar grid shows for <paramref name="date"/> (navigating months if needed).</summary>
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

    private static WorkScheduleProfile ScheduleAt(List<WorkScheduleProfile> profiles, DateOnly date) =>
        ProfileVersioning.Resolve(profiles, date, p => p.EffectiveFrom, () => new WorkScheduleProfile());

    // ── 1. SettingsViewModel reads from ConfigurationDraft ──────────────────────────────

    [Fact]
    public void SettingsVM_ReadsFromConfigurationDraft_NotFromDisk()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var vm = new SettingsViewModel(store, mainVm);

        // VM initialized from store/draft state (default schedule 09:00-18:00)
        Assert.Equal(new TimeOnly(9, 0), vm.WorkStart);
        Assert.Equal(new TimeOnly(18, 0), vm.WorkEnd);
        // Draft base is a deep clone — scalar values match the store.
        Assert.Equal(store.CurrentSettings.DisplayMode, vm.Draft.Base.DisplayMode);
        Assert.Equal(store.CurrentSettings.Language, vm.Draft.Base.Language);
        Assert.Equal(store.CurrentSettings.Theme, vm.Draft.Base.Theme);

        // Mutate the draft the way ScheduleManager does — VM must follow the draft, not disk
        var today = DateOnly.FromDateTime(DateTime.Now);
        vm.Draft.ScheduleProfiles = ProfileVersioning.Upsert(
            vm.Draft.ScheduleProfiles,
            new WorkScheduleProfile { Name = "夏季", WorkStart = new TimeOnly(7, 30), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = today },
            p => p.EffectiveFrom, (a, b) => a.Id == b.Id);
        vm.RefreshFromDraft();

        Assert.Equal(new TimeOnly(7, 30), vm.WorkStart);
        Assert.Equal(new TimeOnly(17, 0), vm.WorkEnd);

        // Draft mutations never touch disk
        Assert.False(File.Exists(SettingsFilePath(_tempDir)));
    }

    // ── 2. WorkWeek edit refreshes CalendarViewModel via the shared draft ───────────────

    [Fact]
    public void WorkWeekEdit_RefreshesCalendarViewModel()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var vm = new SettingsViewModel(store, mainVm);
        // Same wiring as SettingsWindow: the calendar page previews the VM's shared draft
        var calendar = new CalendarViewModel(store, mainVm, vm.Draft);
        var saturday = HolidayFreeSaturday();

        // Default double rest → Saturday rests
        Assert.Equal(DayStatus.Rest, CalendarStatus(calendar, saturday));

        // User switches to single rest on the salary page (unsaved) → draft gets an
        // effective-today policy → calendar preview re-renders automatically
        vm.WeekType = WorkWeekType.SingleRest;

        Assert.Equal(DayStatus.Work, CalendarStatus(calendar, saturday));

        // Draft holds the edit; disk still untouched
        Assert.False(File.Exists(SettingsFilePath(_tempDir)));
    }

    [Fact]
    public void CustomWorkDayEdit_RefreshesCalendarPreview()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var vm = new SettingsViewModel(store, mainVm);
        var calendar = new CalendarViewModel(store, mainVm, vm.Draft);
        var saturday = HolidayFreeSaturday();

        vm.WeekType = WorkWeekType.Custom;
        vm.WorkSaturday = true; // custom week: Saturday works

        // Draft preview AND the live calendar grid agree
        Assert.Equal(DayStatus.Work, vm.Draft.BuildPreviewConfiguration(store.PayData).ResolveDayStatus(saturday));
        Assert.Equal(DayStatus.Work, CalendarStatus(calendar, saturday));
    }

    // ── 3. ConfigurationStore commit refreshes MainViewModel ────────────────────────────

    [Fact]
    public void StoreCommit_RefreshesMainViewModel()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        Assert.Equal(new TimeOnly(9, 0), mainVm.WorkStart);

        var today = DateOnly.FromDateTime(DateTime.Now);
        store.Commit(store.CurrentSettings with
        {
            ScheduleProfiles = ProfileVersioning.Upsert(
                store.CurrentSettings.ScheduleProfiles,
                new WorkScheduleProfile { Name = "夏季", WorkStart = new TimeOnly(7, 30), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = today },
                p => p.EffectiveFrom, (a, b) => a.Id == b.Id),
        });

        Assert.Equal(new TimeOnly(7, 30), mainVm.WorkStart);
        Assert.Equal(new TimeOnly(17, 0), mainVm.WorkEnd);
    }

    // ── 4. Schedule activation syncs SettingsViewModel work times ───────────────────────

    [Fact]
    public void ScheduleActivation_SyncsSettingsVMWorkTimes()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var vm = new SettingsViewModel(store, mainVm);
        var today = DateOnly.FromDateTime(DateTime.Now);

        Assert.Equal(new TimeOnly(9, 0), vm.WorkStart);

        // "设为当前" from the schedule manager: upsert effective-today into the shared draft
        vm.Draft.ScheduleProfiles = ProfileVersioning.Upsert(
            vm.Draft.ScheduleProfiles,
            new WorkScheduleProfile { Name = "🌞 夏季作息", WorkStart = new TimeOnly(7, 30), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = today },
            p => p.EffectiveFrom, (a, b) => a.Id == b.Id);
        vm.RefreshFromDraft();

        Assert.Equal(new TimeOnly(7, 30), vm.WorkStart);
        Assert.Equal(new TimeOnly(17, 0), vm.WorkEnd);
    }

    // ── 5. Cancel does not persist ──────────────────────────────────────────────────────

    [Fact]
    public void Cancel_DoesNotPersist()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var vm = new SettingsViewModel(store, mainVm);

        var revisionBefore = store.Revision;
        var settingsBefore = store.CurrentSettings;
        var events = 0;
        store.ConfigurationChanged += () => events++;

        vm.AmountText = "9000";
        vm.WorkStart = new TimeOnly(6, 0);
        vm.CancelCommand.Execute(null);

        Assert.False(File.Exists(SettingsFilePath(_tempDir)));
        Assert.Equal(revisionBefore, store.Revision);
        Assert.Same(settingsBefore, store.CurrentSettings);
        Assert.Equal(0, events);
        Assert.Equal(6000m, store.CurrentConfiguration.ResolveSalaryProfile(DateOnly.FromDateTime(DateTime.Now)).MonthlyAmount);
    }

    // ── 6. Save commits exactly once; every subscriber sees the same revision/config ────

    [Fact]
    public void Save_CommitsOnce_AllSubscribersSeeSameState()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var vm = new SettingsViewModel(store, mainVm);

        var revisionBefore = store.Revision;
        var events = 0;
        store.ConfigurationChanged += () => events++;

        vm.AmountText = "9000";
        vm.WorkStart = new TimeOnly(7, 30);
        vm.WorkEnd = new TimeOnly(17, 0);
        vm.SaveCommand.Execute(null);

        // Exactly one commit / one change event
        Assert.Equal(revisionBefore + 1, store.Revision);
        Assert.Equal(1, events);

        // MainViewModel (subscriber) shows the same configuration the store holds
        Assert.Equal(new TimeOnly(7, 30), mainVm.WorkStart);

        // Store configuration is the committed one
        Assert.Equal(new TimeOnly(7, 30), store.CurrentConfiguration.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now)).WorkStart);
        Assert.Equal(9000m, store.CurrentConfiguration.ResolveSalaryProfile(DateOnly.FromDateTime(DateTime.Now)).MonthlyAmount);

        // Persisted exactly once — a fresh service on the same directory reads identical state
        var reloaded = new SettingsService(_tempDir).Load();
        Assert.Equal(9000m, reloaded.SalaryProfiles.Max(p => p.MonthlyAmount));
        Assert.Equal(new TimeOnly(7, 30), ScheduleAt(reloaded.ScheduleProfiles, DateOnly.FromDateTime(DateTime.Now)).WorkStart);
    }

    // ── 7. Hotkey registration failure is visible in the settings VM ────────────────────

    [Fact]
    public void HotkeyFailure_IsVisibleInSettingsVM()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var vm = new SettingsViewModel(store, mainVm);

        // Never registered → empty status
        Assert.Equal(string.Empty, vm.HotkeyStatus);

        // Registration failed → non-empty warning naming the occupied combination
        // (in this resource-less test host the lookup falls back to the resource key;
        //  the real app resolves it to "⚠ Ctrl+Alt+X 已被其他程序占用，当前未生效")
        HotkeyService.LastRegistrationSucceeded = false;
        vm.RefreshHotkeyStatus();
        var conflictStatus = vm.HotkeyStatus;
        Assert.False(string.IsNullOrEmpty(conflictStatus));
        Assert.Equal("Settings.Hotkey.Conflict", conflictStatus);

        // Registered → distinct success status
        HotkeyService.LastRegistrationSucceeded = true;
        vm.RefreshHotkeyStatus();
        Assert.NotEqual(conflictStatus, vm.HotkeyStatus);
        Assert.Equal("Settings.Hotkey.Ok", vm.HotkeyStatus);

        // The status text is bound in SettingsWindow.xaml (System tab)
        Assert.NotEqual(string.Empty, HotkeyService.Format(vm.HotkeyModifiers, vm.HotkeyVirtualKey));
    }

    // ── 8. ConfigurationChanged subscription lifecycle ──────────────────────────────────

    [Fact]
    public void MainViewModel_Dispose_UnsubscribesFromStore()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        Assert.Equal(new TimeOnly(9, 0), mainVm.WorkStart);

        mainVm.Dispose();

        // After dispose, commits must NOT propagate into the disposed view model
        var today = DateOnly.FromDateTime(DateTime.Now);
        store.Commit(store.CurrentSettings with
        {
            ScheduleProfiles = ProfileVersioning.Upsert(
                store.CurrentSettings.ScheduleProfiles,
                new WorkScheduleProfile { Name = "夏季", WorkStart = new TimeOnly(7, 30), WorkEnd = new TimeOnly(17, 0), EffectiveFrom = today },
                p => p.EffectiveFrom, (a, b) => a.Id == b.Id),
        });

        Assert.Equal(new TimeOnly(9, 0), mainVm.WorkStart); // stale on purpose: detached
        Assert.Equal(new TimeOnly(7, 30), store.CurrentConfiguration.ResolveSchedule(today).WorkStart);
    }

    [Fact]
    public void Store_CommitNotifiesAllSubscribersWithoutDuplicates()
    {
        var store = CreateStore();
        var mainVm = new MainViewModel(store);
        var events = 0;
        store.ConfigurationChanged += () => events++;

        store.Commit(store.CurrentSettings);
        Assert.Equal(1, events);

        // Disposing the only VM then committing again must not throw or double-fire
        mainVm.Dispose();
        store.Commit(store.CurrentSettings);
        Assert.Equal(2, events);
    }
}
