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
            : (schedule.WorkStart, schedule.WorkEnd),
        LeaveKind.Afternoon => schedule.LunchSpan() is { } lunch
            ? (lunch.End, schedule.WorkEnd)
            : (schedule.WorkStart, schedule.WorkEnd),
        LeaveKind.Hours => (Start, End),
        _ => null,
    };
}
