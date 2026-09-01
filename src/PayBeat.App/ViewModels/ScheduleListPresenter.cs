using PayBeat.App.Domain;
using PayBeat.App.Services;

namespace PayBeat.App.ViewModels;

/// <summary>
/// Builds the schedule-manager list rows: committed draft schedules plus (while the user is
/// creating one) the in-window pending new schedule. Pure and unit-testable — the window only
/// wires it to the ListBox.
/// </summary>
public static class ScheduleListPresenter
{
    /// <summary>Badge shown for a row the user just created inside the manager window.</summary>
    public const string BadgeUnsaved = "Schedule.BadgeUnsaved";

    /// <summary>Badge shown for a row saved into the ConfigurationDraft but not yet committed
    /// by the main settings save.</summary>
    public const string BadgePending = "Schedule.BadgePending";

    /// <summary>
    /// Creates the in-window pending new schedule from the currently active one: today's date,
    /// a fresh id, and the default "new schedule" name — the user immediately sees a second
    /// card instead of only a bare form.
    /// </summary>
    public static WorkScheduleProfile CreatePending(WorkScheduleProfile active)
    {
        return new WorkScheduleProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = LocalizationService.Get("Schedule.NewName"),
            WorkStart = active.WorkStart,
            WorkEnd = active.WorkEnd,
            LunchBreakEnabled = active.LunchBreakEnabled,
            LunchBreakStart = active.LunchBreakStart,
            LunchBreakEnd = active.LunchBreakEnd,
            EffectiveFrom = DateOnly.FromDateTime(DateTime.Now),
        };
    }

    /// <summary>
    /// Rows for the list: every draft schedule, then the pending new one (if any) with the
    /// "unsaved" badge; ids in <paramref name="pendingSavedIds"/> (saved inside the window,
    /// awaiting the main settings save) show the "pending" badge instead.
    /// </summary>
    public static List<ScheduleRowVm> BuildRows(
        IReadOnlyList<WorkScheduleProfile> draftSchedules,
        string activeId,
        WorkScheduleProfile? pendingNew,
        IReadOnlySet<string> pendingSavedIds)
    {
        var rows = new List<ScheduleRowVm>();
        foreach (var s in draftSchedules)
        {
            var badge = pendingSavedIds.Contains(s.Id) ? BadgePending : null;
            rows.Add(new ScheduleRowVm(s, isActive: s.Id == activeId, badge: badge));
        }

        if (pendingNew is not null)
        {
            rows.Add(new ScheduleRowVm(pendingNew, isActive: false, badge: BadgeUnsaved));
        }

        return rows;
    }
}
