namespace PayBeat.App.Domain;

/// <summary>
/// Pure salary computation engine. Stateless and side-effect free; all money math uses
/// <c>decimal</c>. Given a <see cref="PayConfiguration"/>, it computes:
/// <list type="bullet">
///   <item>a day's final target/leave deduction (<see cref="ComputeDay"/>)</item>
///   <item>real-time earned/progress at an instant (<see cref="ComputeDayAt"/>)</item>
///   <item>month aggregates: planned workdays, expected total, earned to date (<see cref="ComputeMonth"/>)</item>
/// </list>
/// Semantics: real-time earned = dailyRate × (completed effective work seconds, leave excluded)
/// ÷ standard effective seconds; accrual caps exactly at the day target after work end;
/// paid time off ("调休") days are credited the full daily rate with no real-time accrual.
/// </summary>
public static class SalaryEngine
{
    /// <summary>
    /// Money noise floor: repeating-decimal daily rates (e.g. 6000/26) accumulate trailing
    /// noise like 6000.0000000000000000000000007 when summed. Rounding every outgoing amount
    /// to 10 decimal places removes it while keeping precision far beyond display needs.
    /// </summary>
    internal static decimal NormalizeMoney(decimal value) => Math.Round(value, 10);

    /// <summary>Computes the final (end-of-day) result for <paramref name="date"/>.</summary>
    public static DayComputation ComputeDay(PayConfiguration config, DateOnly date)
    {
        var status = config.ResolveDayStatus(date);
        var schedule = config.ResolveSchedule(date);
        var rate = config.StandardDailyRate(date);
        var total = status is DayStatus.Work or DayStatus.MakeupWork or DayStatus.Leave
            ? schedule.EffectiveWorkSeconds()
            : 0;

        var leaveSeconds = 0d;
        if (status == DayStatus.Leave && config.ResolveLeave(date) is { } leave && total > 0)
        {
            leaveSeconds = EffectiveLeaveSeconds(leave, schedule);
        }

        return new DayComputation
        {
            Date = date,
            Status = status,
            Schedule = schedule,
            DailyRate = rate,
            TotalEffectiveSeconds = total,
            LeaveSeconds = leaveSeconds,
        };
    }

    /// <summary>Computes real-time progress for <paramref name="date"/> as of <paramref name="time"/>.</summary>
    public static DayProgress ComputeDayAt(PayConfiguration config, DateOnly date, TimeOnly time)
    {
        var computation = ComputeDay(config, date);
        var schedule = computation.Schedule;
        var status = computation.Status;

        // Paid time off: fully credited regardless of time; no real-time accrual.
        if (status == DayStatus.PaidTimeOff)
        {
            return new DayProgress
            {
                Computation = computation,
                Phase = DayPhase.PaidTimeOff,
                Earned = computation.DailyRate,
                WorkedSeconds = 0,
                RemainingSeconds = 0,
            };
        }

        if (status is DayStatus.Rest or DayStatus.PublicHoliday)
        {
            return new DayProgress
            {
                Computation = computation,
                Phase = DayPhase.OffDay,
                Earned = 0m,
                WorkedSeconds = 0,
                RemainingSeconds = 0,
            };
        }

        // Paid day or leave day: accrue over effective spans, excluding leave spans.
        var total = computation.TotalEffectiveSeconds;
        var nowSeconds = SecondsOf(time);

        // Phase relative to the wall-clock work window.
        DayPhase phase;
        if (computation.Status == DayStatus.Leave && computation.LeaveSeconds >= total)
        {
            phase = DayPhase.AfterWork; // full-day leave: nothing accrues all day
        }
        else if (nowSeconds <= SecondsOf(schedule.WorkStart))
        {
            phase = DayPhase.BeforeWork;
        }
        else if (nowSeconds >= SecondsOf(schedule.WorkEnd))
        {
            phase = DayPhase.AfterWork;
        }
        else if (schedule.LunchSpan() is { } lunch
                 && nowSeconds >= SecondsOf(lunch.Start) && nowSeconds < SecondsOf(lunch.End)
                 && !IsLeaveAt(config, computation, time))
        {
            phase = DayPhase.Lunch;
        }
        else
        {
            phase = DayPhase.Working;
        }

        // Completed effective seconds: spans ∩ [day start, now] minus already-passed leave time.
        var worked = 0d;
        foreach (var (start, end) in schedule.EffectiveSpans())
        {
            worked += IntersectSeconds(start, end, schedule.WorkStart, time);
        }

        if (computation.LeaveSeconds > 0 && config.ResolveLeave(computation.Date) is { } leave
            && leave.RequestedSpan(schedule) is (var ls, var le))
        {
            var leavePassed = 0d;
            foreach (var (start, end) in schedule.EffectiveSpans())
            {
                // Leave clipped to this paid span, further clipped to what has already passed.
                leavePassed += IntersectSeconds(MaxTime(ls, start), MinTime(le, end), start, time);
            }
            worked = Math.Max(0d, worked - Math.Min(leavePassed, worked));
        }

        var earned = total > 0
            ? NormalizeMoney(computation.DailyRate * ((decimal)worked / (decimal)total))
            : 0m;

        // After work end the earned value must land exactly on the (already-rounded-semantics) target.
        if (phase == DayPhase.AfterWork)
        {
            earned = computation.TargetEarned;
        }
        if (phase == DayPhase.BeforeWork)
        {
            earned = 0m;
        }

        var remaining = Math.Clamp(
            SecondsOf(schedule.WorkEnd) - Math.Max(nowSeconds, SecondsOf(schedule.WorkStart)),
            0d,
            SecondsOf(schedule.WorkEnd) - SecondsOf(schedule.WorkStart));

        return new DayProgress
        {
            Computation = computation,
            Phase = phase,
            Earned = earned,
            WorkedSeconds = Math.Min(worked, total),
            RemainingSeconds = remaining,
        };
    }

    /// <summary>Monthly aggregate for <paramref name="month"/>.</summary>
    /// <param name="today">Current local date; days after this are not counted as earned.</param>
    /// <param name="now">Current local time-of-day used for today's live accrual.</param>
    public static MonthSummary ComputeMonth(PayConfiguration config, DateOnly month, DateOnly today, TimeOnly now)
    {
        var planned = config.PlannedWorkdays(month);

        var profile = config.ResolveSalaryProfile(month);
        var standardMonthly = profile.Mode == SalaryMode.Monthly
            ? profile.MonthlyAmount
            : planned > 0 ? profile.DailyAmount * planned : 0m;

        decimal target = 0m;
        decimal earned = 0m;
        var passedWorkdays = 0;
        var ptoDays = 0;
        var leaveSeconds = 0d;

        foreach (var date in PayConfiguration.EachDay(month))
        {
            var day = ComputeDay(config, date);

            target += day.TargetEarned;
            leaveSeconds += day.LeaveSeconds;
            if (day.Status == DayStatus.PaidTimeOff)
            {
                ptoDays++;
            }

            // "Passed" workdays drive the 15/26-style progress: planned workdays strictly before today.
            // Today is NOT counted as "passed" until it completes (via exit snapshot or rollover).
            if (date < today
                && day.Status is DayStatus.Work or DayStatus.MakeupWork
                && day.LeaveSeconds < day.TotalEffectiveSeconds)
            {
                passedWorkdays++;
            }

            if (date < today)
            {
                earned += day.FinalEarned;
            }
            else if (date == today)
            {
                earned += ComputeDayAt(config, date, now).Earned;
            }
        }

        return new MonthSummary
        {
            Month = month,
            PlannedWorkdays = planned,
            StandardMonthly = NormalizeMoney(standardMonthly),
            MonthTarget = NormalizeMoney(target),
            MonthEarned = NormalizeMoney(earned),
            PassedWorkdays = passedWorkdays,
            PtoDays = ptoDays,
            LeaveHours = NormalizeMoney((decimal)(leaveSeconds / 3600.0)),
        };
    }

    /// <summary>Per-day computations for a month (calendar UI).</summary>
    public static IReadOnlyList<DayComputation> ComputeMonthDays(PayConfiguration config, DateOnly month)
    {
        var list = new List<DayComputation>();
        foreach (var date in PayConfiguration.EachDay(month))
        {
            list.Add(ComputeDay(config, date));
        }
        return list;
    }

    /// <summary>Effective deducted seconds of a leave record against the schedule (lunch never deducted).</summary>
    public static double EffectiveLeaveSeconds(LeaveRecord leave, WorkScheduleProfile schedule)
    {
        if (leave.RequestedSpan(schedule) is not (var ls, var le))
        {
            return 0;
        }
        var seconds = 0d;
        foreach (var (start, end) in schedule.EffectiveSpans())
        {
            seconds += IntersectSeconds(ls, le, start, end);
        }
        return seconds;
    }

    /// <summary>Seconds of [aStart, aEnd] ∩ [bStart, bEnd] (0 when disjoint).</summary>
    public static double IntersectSeconds(TimeOnly aStart, TimeOnly aEnd, TimeOnly bStart, TimeOnly bEnd)
    {
        var start = Math.Max(SecondsOf(aStart), SecondsOf(bStart));
        var end = Math.Min(SecondsOf(aEnd), SecondsOf(bEnd));
        return end > start ? end - start : 0d;
    }

    private static bool IsLeaveAt(PayConfiguration config, DayComputation computation, TimeOnly time)
    {
        if (computation.Status != DayStatus.Leave || config.ResolveLeave(computation.Date) is not { } leave)
        {
            return false;
        }
        return leave.RequestedSpan(computation.Schedule) is var (ls, le)
               && SecondsOf(time) >= SecondsOf(ls)
               && SecondsOf(time) < SecondsOf(le);
    }

    private static double SecondsOf(TimeOnly t) => t.Hour * 3600d + t.Minute * 60d + t.Second;

    private static TimeOnly MaxTime(TimeOnly a, TimeOnly b) => SecondsOf(a) >= SecondsOf(b) ? a : b;

    private static TimeOnly MinTime(TimeOnly a, TimeOnly b) => SecondsOf(a) <= SecondsOf(b) ? a : b;
}
