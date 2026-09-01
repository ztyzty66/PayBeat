using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;

namespace PayBeat.Tests;

/// <summary>
/// New-schedule draft UX: clicking "新建方案" must immediately produce a SECOND list card
/// (unsaved badge, template copied from the active schedule), saved-in-window cards show
/// "pending", cancel never pollutes the store, and commit persists both schedules.
/// </summary>
public class NewScheduleDraftTests : IDisposable
{
    private readonly string _tempDir;

    public NewScheduleDraftTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PayBeatNewSch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ConfigurationStore CreateStore() => new(
        new SettingsService(_tempDir),
        new HistoryService(Path.Combine(_tempDir, "history")));

    private static WorkScheduleProfile Summer() => new()
    {
        Id = "summer",
        Name = "🌞 夏季作息",
        WorkStart = new TimeOnly(7, 30),
        WorkEnd = new TimeOnly(17, 0),
        LunchBreakEnabled = true,
        LunchBreakStart = new TimeOnly(11, 15),
        LunchBreakEnd = new TimeOnly(12, 45),
        EffectiveFrom = new DateOnly(2026, 5, 1),
    };

    // 1. NewSchedule_Click_CreatesSecondDraftRow ------------------------------------------

    [Fact]
    public void NewSchedule_CreatesSecondDraftRow()
    {
        var rows = ScheduleListPresenter.BuildRows([Summer()], "summer", pendingNew: null, new HashSet<string>());

        Assert.Single(rows); // before 新建: one card

        var pending = ScheduleListPresenter.CreatePending(Summer());
        rows = ScheduleListPresenter.BuildRows([Summer()], "summer", pending, new HashSet<string>());

        Assert.Equal(2, rows.Count); // after 新建: second card appears immediately
        Assert.Equal("summer", rows[0].Schedule.Id);
        Assert.Equal(pending.Id, rows[1].Schedule.Id);
    }

    // 2. NewSchedule_DefaultsFromActiveSchedule -------------------------------------------

    [Fact]
    public void NewSchedule_DefaultsFromActiveSchedule()
    {
        var pending = ScheduleListPresenter.CreatePending(Summer());

        Assert.NotEqual(Summer().Id, pending.Id); // a NEW schedule, not a mutation of the old one
        Assert.Equal(new TimeOnly(7, 30), pending.WorkStart); // copies the active template…
        Assert.Equal(new TimeOnly(17, 0), pending.WorkEnd);
        Assert.True(pending.LunchBreakEnabled);
        Assert.Equal(new TimeOnly(11, 15), pending.LunchBreakStart);
        Assert.Equal(new TimeOnly(12, 45), pending.LunchBreakEnd);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), pending.EffectiveFrom); // effective today
        // Resource-less test host: the localized name falls back to its key, still clearly
        // distinct from the active schedule's name.
        Assert.NotEqual("🌞 夏季作息", pending.Name);
    }

    // 3. NewSchedule_HasUnsavedState --------------------------------------------------------

    [Fact]
    public void NewSchedule_HasUnsavedState()
    {
        var pending = ScheduleListPresenter.CreatePending(Summer());
        var rows = ScheduleListPresenter.BuildRows([Summer()], "summer", pending, new HashSet<string>());

        var pendingRow = rows.Single(r => r.Schedule.Id == pending.Id);
        Assert.False(pendingRow.IsActive); // cannot be "in use" — it is not even saved
        Assert.Equal("Schedule.BadgeUnsaved", pendingRow.BadgeKey);
        Assert.NotEqual(string.Empty, pendingRow.BadgeText);

        var activeRow = rows.Single(r => r.Schedule.Id == "summer");
        Assert.Null(activeRow.BadgeKey); // active row shows ✅, not a draft badge
        Assert.NotEqual(string.Empty, activeRow.ActiveText);
    }

    // 4. NewSchedule_SaveUpdatesDraftRow ----------------------------------------------------

    [Fact]
    public void NewSchedule_SaveUpdatesDraftRow()
    {
        var pending = ScheduleListPresenter.CreatePending(Summer());
        var saved = pending with
        {
            Name = "❄️ 冬季作息",
            WorkStart = new TimeOnly(8, 0),
            WorkEnd = new TimeOnly(17, 0),
            LunchBreakStart = new TimeOnly(12, 0),
            LunchBreakEnd = new TimeOnly(13, 0),
        };

        // Row save = upsert into the draft, retire the pending entry, mark the id pending
        var pendingSavedIds = new HashSet<string> { saved.Id };
        var draft = ProfileVersioning.Upsert([Summer()], saved, p => p.EffectiveFrom, (a, b) => a.Id == b.Id);

        var rows = ScheduleListPresenter.BuildRows(draft, "summer", pendingNew: null, pendingSavedIds);

        Assert.Equal(2, rows.Count);
        var winterRow = rows.Single(r => r.Schedule.Id == saved.Id);
        Assert.Equal("❄️ 冬季作息", winterRow.Name);
        Assert.Equal("Schedule.BadgePending", winterRow.BadgeKey); // awaiting main settings save
        Assert.False(winterRow.IsActive);
        var summerRow = rows.Single(r => r.Schedule.Id == "summer");
        Assert.True(summerRow.IsActive); // unchanged by the new row
    }

    // 5. SettingsCancel_DiscardsNewSchedule --------------------------------------------------

    [Fact]
    public void SettingsCancel_DiscardsNewSchedule()
    {
        var store = CreateStore();
        var before = store.CurrentSettings;

        // Draft-only mutation (as the manager window performs it) + no main save
        var draft = store.CreateDraft();
        draft.ScheduleProfiles = ProfileVersioning.Upsert(
            draft.ScheduleProfiles,
            ScheduleListPresenter.CreatePending(Summer()) with { Name = "❄️ 冬季作息" },
            p => p.EffectiveFrom, (a, b) => a.Id == b.Id);

        // Simulated cancel: throw the draft away
        Assert.False(File.Exists(Path.Combine(_tempDir, "settings.json")));
        Assert.Equal(0, store.Revision);
        Assert.Same(before, store.CurrentSettings);
        Assert.Single(store.CurrentSettings.ScheduleProfiles);
    }

    // 6 + 7. SettingsCommit_PersistsTwoSchedules + Reopen_ShowsBothSchedules -----------------

    [Fact]
    public void SettingsCommit_PersistsTwoSchedules_AcrossReopen()
    {
        var store = CreateStore();
        var draft = store.CreateDraft();
        draft.ScheduleProfiles = [Summer()]; // the active schedule already exists
        var winter = ScheduleListPresenter.CreatePending(Summer()) with
        {
            Name = "❄️ 冬季作息",
            WorkStart = new TimeOnly(8, 0),
            WorkEnd = new TimeOnly(17, 0),
            LunchBreakEnabled = true,
            LunchBreakStart = new TimeOnly(12, 0),
            LunchBreakEnd = new TimeOnly(13, 0),
            EffectiveFrom = new DateOnly(2026, 10, 1),
        };
        draft.ScheduleProfiles = ProfileVersioning.Upsert(
            draft.ScheduleProfiles, winter, p => p.EffectiveFrom, (a, b) => a.Id == b.Id);
        store.Commit(draft.ToSettings());

        // Reopen: fresh store over the same directory shows both schedules
        var reopened = new ConfigurationStore(
            new SettingsService(_tempDir),
            new HistoryService(Path.Combine(_tempDir, "history")));
        Assert.Equal(2, reopened.CurrentSettings.ScheduleProfiles.Count);

        var rows = ScheduleListPresenter.BuildRows(
            reopened.CurrentSettings.ScheduleProfiles,
            reopened.CurrentConfiguration.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now)).Id,
            pendingNew: null,
            new HashSet<string>());
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Name == "🌞 夏季作息");
        Assert.Contains(rows, r => r.Name == "❄️ 冬季作息");
    }

    // 8. SelectedSchedule_NotEqualActiveSchedule ---------------------------------------------

    [Fact]
    public void SelectingInactiveSchedule_KeepsActiveBadgeOnTheActiveOne()
    {
        var winter = new WorkScheduleProfile
        {
            Id = "winter",
            Name = "❄️ 冬季作息",
            WorkStart = new TimeOnly(8, 0),
            WorkEnd = new TimeOnly(17, 0),
            EffectiveFrom = new DateOnly(2026, 10, 1),
        };

        // "Selected = winter" must not change which row is active: summer keeps ✅ 当前使用,
        // winter carries no badge — the UI distinction is data-driven.
        var rows = ScheduleListPresenter.BuildRows([Summer(), winter], "summer", pendingNew: null, new HashSet<string>());

        var summerRow = rows.Single(r => r.Schedule.Id == "summer");
        var winterRow = rows.Single(r => r.Schedule.Id == "winter");
        Assert.True(summerRow.IsActive);
        Assert.NotEqual(string.Empty, summerRow.ActiveText);
        Assert.False(winterRow.IsActive);
        Assert.Equal(string.Empty, winterRow.ActiveText);
        Assert.Null(winterRow.BadgeKey);
    }
}
