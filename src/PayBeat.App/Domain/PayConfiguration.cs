namespace PayBeat.App.Domain;

/// <summary>Where a calendar day's resolved status comes from (priority chain order).</summary>
public enum DayStatusSource
{
    /// <summary>User set this day manually in the calendar/day editor.</summary>
    ManualOverride = 0,

    /// <summary>Official public holiday dataset marks the day off.</summary>
    PublicHoliday = 1,

    /// <summary>Official holiday dataset marks the day as a makeup workday.</summary>
    MakeupWork = 2,

    /// <summary>Derived from the effective weekly work policy.</summary>
    WeekPolicy = 3,

    /// <summary>No policy covers the date; engine defaults apply (weekdays work, weekends rest).</summary>
    DefaultRule = 4,
}

/// <summary>
/// Aggregated, immutable view of all user configuration relevant to pay computation:
/// versioned salary profiles, schedule profiles, week policies, per-day overrides, and the
/// official holiday calendar. Provides effective-date resolution and day-status resolution
/// honoring the fixed priority chain:
/// <list type="number">
///   <item>user manual override (calendar/leave/PTO)</item>
///   <item>official holiday calendar</item>
///   <item>weekly work policy</item>
///   <item>default (weekdays work, weekends rest)</item>
/// </list>
/// </summary>
public sealed record PayConfiguration
{
    public const string DefaultScheduleId = "default";

    public required IReadOnlyList<SalaryProfile> SalaryProfiles { get; init; }

    public required IReadOnlyList<WorkScheduleProfile> ScheduleProfiles { get; init; }

    public required IReadOnlyList<WorkWeekPolicy> WeekPolicies { get; init; }

    public required IReadOnlyDictionary<DateOnly, CalendarOverride> Overrides { get; init; }

    public required HolidayCalendar Holidays { get; init; }

    /// <summary>Used only as the UI label of the legacy (migrated) schedule profile.</summary>
    public string LegacyScheduleName { get; init; } = "";

    /// <summary>Returns the salary profile effective on <paramref name="date"/> (latest EffectiveFrom ≤ date).</summary>
    public SalaryProfile ResolveSalaryProfile(DateOnly date)
    {
        SalaryProfile? best = null;
        foreach (var p in SalaryProfiles)
        {
            if (p.EffectiveFrom <= date && (best is null || p.EffectiveFrom > best.EffectiveFrom))
            {
                best = p;
            }
        }
        return best ?? new SalaryProfile();
    }

    /// <summary>Returns the schedule effective on <paramref name="date"/> (latest EffectiveFrom ≤ date).</summary>
    public WorkScheduleProfile ResolveSchedule(DateOnly date)
    {
        WorkScheduleProfile? best = null;
        foreach (var s in ScheduleProfiles)
        {
            if (s.EffectiveFrom <= date && (best is null || s.EffectiveFrom > best.EffectiveFrom))
            {
                best = s;
            }
        }
        return best ?? new WorkScheduleProfile();
    }

    /// <summary>Returns the week policy effective on <paramref name="date"/>.</summary>
    public WorkWeekPolicy ResolveWeekPolicy(DateOnly date)
    {
        WorkWeekPolicy? best = null;
        foreach (var p in WeekPolicies)
        {
            if (p.EffectiveFrom <= date && (best is null || p.EffectiveFrom > best.EffectiveFrom))
            {
                best = p;
            }
        }
        return best ?? WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1));
    }

    /// <summary>
    /// Resolves the status of <paramref name="date"/> through the priority chain:
    /// override &gt; official holiday &gt; weekly policy &gt; weekday default.
    /// </summary>
    public DayStatus ResolveDayStatus(DateOnly date)
    {
        if (Overrides.TryGetValue(date, out var ov))
        {
            return ov.Status;
        }

        if (Holidays.Get(date) is { } holiday)
        {
            return holiday.IsOffDay ? DayStatus.PublicHoliday : DayStatus.MakeupWork;
        }

        return ResolveWeekPolicy(date).WorkDays.Contains(date.DayOfWeek)
            ? DayStatus.Work
            : DayStatus.Rest;
    }

    /// <summary>Returns the leave record (if any) attached to <paramref name="date"/>.</summary>
    public LeaveRecord? ResolveLeave(DateOnly date) =>
        Overrides.TryGetValue(date, out var ov) ? ov.Leave : null;

    /// <summary>
    /// Explains why <see cref="ResolveDayStatus"/> produced its result: manual override first,
    /// then the official holiday dataset, then the weekly work policy, then the built-in default.
    /// Pure presentation metadata — the resolved status and its priority chain are unchanged.
    /// </summary>
    public DayStatusSource ResolveDayStatusSource(DateOnly date)
    {
        if (Overrides.ContainsKey(date))
        {
            return DayStatusSource.ManualOverride;
        }

        if (Holidays.Get(date) is { } holiday)
        {
            return holiday.IsOffDay ? DayStatusSource.PublicHoliday : DayStatusSource.MakeupWork;
        }

        foreach (var p in WeekPolicies)
        {
            if (p.EffectiveFrom <= date)
            {
                return DayStatusSource.WeekPolicy;
            }
        }

        return DayStatusSource.DefaultRule;
    }

    /// <summary>
    /// Status of <paramref name="date"/> as *planned* — i.e. ignoring absence overrides entirely.
    /// Leave AND paid time off are absences/paid rest on planned days, not changes of plan: the
    /// standard daily rate must keep using the same planned-workday denominator after leave is
    /// taken (no re-averaging), and PTO days stay fully paid within the plan (no month reduction).
    /// Forced work/rest/holiday overrides DO change the plan, so they are kept.
    /// </summary>
    public DayStatus ResolvePlannedStatus(DateOnly date)
    {
        if (Overrides.TryGetValue(date, out var ov)
            && ov.Status is not (DayStatus.Leave or DayStatus.PaidTimeOff))
        {
            return ov.Status;
        }

        if (Holidays.Get(date) is { } holiday)
        {
            return holiday.IsOffDay ? DayStatus.PublicHoliday : DayStatus.MakeupWork;
        }

        return ResolveWeekPolicy(date).WorkDays.Contains(date.DayOfWeek)
            ? DayStatus.Work
            : DayStatus.Rest;
    }

    /// <summary>
    /// Counts planned paid workdays of <paramref name="month"/>: days whose *planned* status is
    /// <see cref="DayStatus.Work"/> or <see cref="DayStatus.MakeupWork"/>. Leave days stay in the
    /// plan (they were planned workdays), so the monthly daily rate never re-averages; paid time
    /// off days stay too (they remain fully paid).
    /// </summary>
    public int PlannedWorkdays(DateOnly month)
    {
        var count = 0;
        foreach (var date in EachDay(month))
        {
            if (ResolvePlannedStatus(date) is DayStatus.Work or DayStatus.MakeupWork)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Returns the standard daily rate for <paramref name="date"/> (Decimal, unrounded beyond division).</summary>
    public decimal StandardDailyRate(DateOnly date)
    {
        var profile = ResolveSalaryProfile(date);
        if (profile.Mode == SalaryMode.Daily)
        {
            return profile.DailyAmount;
        }

        var planned = PlannedWorkdays(new DateOnly(date.Year, date.Month, 1));
        return planned > 0 ? profile.MonthlyAmount / planned : 0m;
    }

    internal static IEnumerable<DateOnly> EachDay(DateOnly month)
    {
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        for (var d = 1; d <= days; d++)
        {
            yield return new DateOnly(month.Year, month.Month, d);
        }
    }
}
