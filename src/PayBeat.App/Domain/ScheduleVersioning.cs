namespace PayBeat.App.Domain;

/// <summary>
/// Pure domain logic for schedule version operations: activate, edit, delete.
/// Extracted from ScheduleManagerWindow code-behind so WPF and unit tests share the
/// same invariants. All methods are pure functions that return new lists — no side effects.
/// </summary>
public static class ScheduleVersioning
{
    /// <summary>
    /// Activates a schedule as the current effective schedule (from today onward).
    /// The original entry is preserved at its original EffectiveFrom (for historical entries)
    /// or removed (for future entries that are being superseded). A new version is created
    /// with EffectiveFrom = today and a new Id.
    /// </summary>
    /// <returns>The updated schedule list, or null if the schedule was not found.</returns>
    public static List<WorkScheduleProfile>? Activate(
        List<WorkScheduleProfile> schedules,
        string scheduleId,
        DateOnly today)
    {
        var selected = schedules.FirstOrDefault(s => s.Id == scheduleId);
        if (selected is null) return null;

        // Already active for today: no-op.
        if (selected.EffectiveFrom == today) return new List<WorkScheduleProfile>(schedules);

        // For historical entries, preserve the original. For future entries, remove it
        // (it is being superseded by the new today-dated version).
        var filtered = selected.EffectiveFrom < today
            ? new List<WorkScheduleProfile>(schedules) // keep everything including original
            : schedules.Where(s => s.Id != selected.Id).ToList(); // remove future entry

        // Create a new version effective from today with a fresh Id.
        var activated = selected with { Id = Guid.NewGuid().ToString("N"), EffectiveFrom = today };
        var result = ProfileVersioning.Upsert(filtered, activated, s => s.EffectiveFrom);
        return ProfileVersioning.DeduplicateByDate(result, s => s.EffectiveFrom);
    }

    /// <summary>
    /// Edits an existing schedule. For historical versions (EffectiveFrom &lt; today),
    /// a new version is created and the original is preserved untouched.
    /// For today/future versions, the entry is replaced in-place.
    /// </summary>
    /// <returns>The updated schedule list.</returns>
    public static List<WorkScheduleProfile> Edit(
        List<WorkScheduleProfile> schedules,
        WorkScheduleProfile edited,
        DateOnly today)
    {
        var index = schedules.FindIndex(s => s.Id == edited.Id);
        if (index < 0) return schedules;

        var existing = schedules[index];

        if (existing.EffectiveFrom < today)
        {
            // Historical: never rewrite — keep original, create new version effective from today.
            // The user's edit applies from today onward; past dates keep the original.
            var updated = edited with { Id = Guid.NewGuid().ToString("N"), EffectiveFrom = today };
            var result = new List<WorkScheduleProfile>(schedules) { updated };
            return result;
        }
        else
        {
            // Today/future: replace in-place (same Id, same date).
            var result = new List<WorkScheduleProfile>(schedules);
            result[index] = edited;
            return ProfileVersioning.DeduplicateByDate(result, s => s.EffectiveFrom);
        }
    }

    /// <summary>
    /// Attempts to delete a schedule. Returns false if the schedule is the currently
    /// active one (resolved for today) or is a historical entry (EffectiveFrom &lt; today).
    /// </summary>
    /// <returns>
    /// A tuple: (success, updated list). If success is false, the list is unchanged.
    /// </returns>
    public static (bool Success, List<WorkScheduleProfile> Schedules) Delete(
        List<WorkScheduleProfile> schedules,
        string scheduleId,
        DateOnly today,
        PayConfiguration config)
    {
        var selected = schedules.FirstOrDefault(s => s.Id == scheduleId);
        if (selected is null) return (false, schedules);

        // Cannot delete the currently active schedule.
        var activeId = config.ResolveSchedule(today).Id;
        if (selected.Id == activeId) return (false, schedules);

        // Cannot delete historical versions (EffectiveFrom < today).
        if (selected.EffectiveFrom < today) return (false, schedules);

        // Future versions can be deleted.
        var result = schedules.Where(s => s.Id != selected.Id).ToList();
        return (true, result);
    }
}
