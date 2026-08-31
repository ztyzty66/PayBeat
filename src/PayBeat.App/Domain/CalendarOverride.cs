namespace PayBeat.App.Domain;

/// <summary>
/// A user-authored calendar override for one specific date. Overrides have the highest priority:
/// they beat the official holiday calendar, the weekly policy, and any default logic.
/// </summary>
public record CalendarOverride
{
    /// <summary>Date this override applies to (local time).</summary>
    public DateOnly Date { get; init; }

    /// <summary>Status forced onto the day.</summary>
    public DayStatus Status { get; init; }

    /// <summary>Leave detail. Non-null only when <see cref="Status"/> is <see cref="DayStatus.Leave"/>.</summary>
    public LeaveRecord? Leave { get; init; }

    /// <summary>Convenience factory: a plain status override (work/rest/holiday/makeup/PTO).</summary>
    public static CalendarOverride For(DateOnly date, DayStatus status) => new()
    {
        Date = date,
        Status = status,
    };

    /// <summary>Convenience factory: a leave override.</summary>
    public static CalendarOverride LeaveOverride(DateOnly date, LeaveRecord leave) => new()
    {
        Date = date,
        Status = DayStatus.Leave,
        Leave = leave,
    };
}

/// <summary>
/// A leave record for one day. The effective deducted time is the intersection of the requested
/// span with the day's paid work spans — lunch break time is never deducted.
/// </summary>
/// <param name="Kind">Leave granularity (full day / morning / afternoon / custom hours).</param>
/// <param name="Start">Custom range start (only for <see cref="LeaveKind.Hours"/>).</param>
/// <param name="End">Custom range end (only for <see cref="LeaveKind.Hours"/>).</param>
public record LeaveRecord(LeaveKind Kind, TimeOnly Start = default, TimeOnly End = default)
{
    /// <summary>Requested wall-clock span for this leave, resolved against the day's schedule.</summary>
    public (TimeOnly Start, TimeOnly End)? RequestedSpan(WorkScheduleProfile schedule) => Kind switch
    {
        LeaveKind.FullDay => (schedule.WorkStart, schedule.WorkEnd),
        LeaveKind.Morning => schedule.LunchSpan() is { } lunch
            ? (schedule.WorkStart, lunch.Start)
            : HalfDaySplit(schedule, isMorning: true),
        LeaveKind.Afternoon => schedule.LunchSpan() is { } lunch
            ? (lunch.End, schedule.WorkEnd)
            : HalfDaySplit(schedule, isMorning: false),
        LeaveKind.Hours => (Start, End),
        _ => null,
    };

    /// <summary>
    /// When no lunch break is configured, splits the work window at the effective-work-seconds
    /// midpoint so Morning = first half, Afternoon = second half, and both together equal
    /// the full effective work time.
    /// </summary>
    private static (TimeOnly Start, TimeOnly End) HalfDaySplit(WorkScheduleProfile schedule, bool isMorning)
    {
        // For schedules without lunch, EffectiveWorkSeconds == (WorkEnd - WorkStart) in seconds.
        var totalSeconds = schedule.EffectiveWorkSeconds();
        var halfSeconds = totalSeconds / 2.0;

        var startSeconds = schedule.WorkStart.Hour * 3600 + schedule.WorkStart.Minute * 60;
        if (isMorning)
        {
            var endSeconds = startSeconds + (int)Math.Ceiling(halfSeconds);
            return (schedule.WorkStart, SecondsToTimeOnly(endSeconds));
        }
        else
        {
            var midSeconds = startSeconds + (int)Math.Floor(halfSeconds);
            return (SecondsToTimeOnly(midSeconds), schedule.WorkEnd);
        }
    }

    private static TimeOnly SecondsToTimeOnly(int totalSeconds)
    {
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        return new TimeOnly(Math.Clamp(hours, 0, 23), Math.Clamp(minutes, 0, 59));
    }

    /// <summary>
    /// Validates this leave record against the schedule. Returns null if valid, or a
    /// localized error message if invalid.
    /// </summary>
    public string? Validate(WorkScheduleProfile schedule, Func<string, string> localize)
    {
        if (Kind != LeaveKind.Hours) return null;

        if (Start >= End)
        {
            return localize("Error.LeaveStartAfterEnd");
        }

        // Check that the requested span intersects with at least one effective work span.
        var hasOverlap = false;
        foreach (var (ws, we) in schedule.EffectiveSpans())
        {
            if (Start < we && End > ws) { hasOverlap = true; break; }
        }

        if (!hasOverlap)
        {
            return localize("Error.LeaveOutsideWorkHours");
        }

        return null;
    }
}
