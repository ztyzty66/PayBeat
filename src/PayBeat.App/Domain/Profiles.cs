namespace PayBeat.App.Domain;

/// <summary>
/// A versioned salary configuration. Takes effect from <see cref="EffectiveFrom"/> (inclusive)
/// until the next profile's effective date. Historical months always resolve the profile that was
/// effective at that time, so changing today's salary never rewrites the past.
/// </summary>
public record SalaryProfile
{
    /// <summary>Salary expression mode.</summary>
    public SalaryMode Mode { get; init; } = SalaryMode.Monthly;

    /// <summary>Standard monthly amount (used when <see cref="Mode"/> is <see cref="SalaryMode.Monthly"/>).</summary>
    public decimal MonthlyAmount { get; init; }

    /// <summary>Standard daily amount (used when <see cref="Mode"/> is <see cref="SalaryMode.Daily"/>).</summary>
    public decimal DailyAmount { get; init; }

    /// <summary>First date (inclusive) on which this profile applies.</summary>
    public DateOnly EffectiveFrom { get; init; } = new(2000, 1, 1);

    /// <summary>Returns the base daily amount for <paramref name="month"/>. In monthly mode the
    /// caller divides by the planned paid workdays of that month; in daily mode the amount is used as-is.</summary>
    public decimal BaseAmountFor(DateOnly month) =>
        Mode == SalaryMode.Monthly ? MonthlyAmount : DailyAmount;
}

/// <summary>
/// A named daily work-time schedule (summer/winter etc.) with an effective date.
/// Historical months always resolve the schedule that was effective at that time.
/// </summary>
public record WorkScheduleProfile
{
    /// <summary>Stable identifier (GUID string) used for editing/deleting.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name, e.g. "夏季作息".</summary>
    public string Name { get; init; } = "";

    public TimeOnly WorkStart { get; init; } = new(9, 0);

    public TimeOnly WorkEnd { get; init; } = new(18, 0);

    public bool LunchBreakEnabled { get; init; }

    public TimeOnly LunchBreakStart { get; init; } = new(12, 0);

    public TimeOnly LunchBreakEnd { get; init; } = new(13, 0);

    /// <summary>First date (inclusive) on which this schedule applies.</summary>
    public DateOnly EffectiveFrom { get; init; } = new(2000, 1, 1);

    /// <summary>Total effective (paid) work seconds of this schedule after lunch deduction.</summary>
    public double EffectiveWorkSeconds()
    {
        var total = (WorkEnd - WorkStart).TotalSeconds;
        if (!LunchBreakEnabled)
        {
            return total;
        }
        if (LunchBreakEnd <= LunchBreakStart || LunchBreakStart < WorkStart || LunchBreakEnd > WorkEnd)
        {
            return total;
        }
        return total - (LunchBreakEnd - LunchBreakStart).TotalSeconds;
    }

    /// <summary>
    /// Effective (paid) intervals of the day as (start, end) second offsets from midnight.
    /// Without lunch this is the single [WorkStart, WorkEnd] span; with a valid lunch the
    /// [WorkStart, LunchStart) and [LunchEnd, WorkEnd] spans are returned.
    /// </summary>
    public List<(TimeOnly Start, TimeOnly End)> EffectiveSpans()
    {
        if (!LunchBreakEnabled
            || LunchBreakEnd <= LunchBreakStart
            || LunchBreakStart <= WorkStart
            || LunchBreakEnd >= WorkEnd)
        {
            return [(WorkStart, WorkEnd)];
        }

        return [(WorkStart, LunchBreakStart), (LunchBreakEnd, WorkEnd)];
    }

    /// <summary>Clamped lunch interval, or <see langword="null"/> when lunch is disabled/invalid.</summary>
    public (TimeOnly Start, TimeOnly End)? LunchSpan()
    {
        if (!LunchBreakEnabled
            || LunchBreakEnd <= LunchBreakStart
            || LunchBreakStart <= WorkStart
            || LunchBreakEnd >= WorkEnd)
        {
            return null;
        }
        return (LunchBreakStart, LunchBreakEnd);
    }
}

/// <summary>
/// A versioned weekly work/rest policy. <see cref="WorkDays"/> holds the working weekdays so every
/// preset (double rest, single rest, custom) is expressed uniformly and every rest day is editable.
/// </summary>
public record WorkWeekPolicy
{
    /// <summary>Preset type (drives which UI controls are shown).</summary>
    public WorkWeekType Type { get; init; } = WorkWeekType.DoubleRest;

    /// <summary>Weekdays on which work is normally scheduled.</summary>
    public HashSet<DayOfWeek> WorkDays { get; init; } = [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday,
    ];

    /// <summary>First date (inclusive) on which this policy applies.</summary>
    public DateOnly EffectiveFrom { get; init; } = new(2000, 1, 1);

    /// <summary>Returns the default policy for a preset type.</summary>
    public static WorkWeekPolicy Create(WorkWeekType type, DateOnly effectiveFrom)
    {
        WorkWeekPolicy policy = type switch
        {
            WorkWeekType.DoubleRest => new WorkWeekPolicy
            {
                Type = type,
                WorkDays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
                EffectiveFrom = effectiveFrom,
            },
            WorkWeekType.SingleRest => new WorkWeekPolicy
            {
                Type = type,
                WorkDays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday],
                EffectiveFrom = effectiveFrom,
            },
            _ => new WorkWeekPolicy
            {
                Type = type,
                WorkDays = [],
                EffectiveFrom = effectiveFrom,
            },
        };
        return policy;
    }
}
