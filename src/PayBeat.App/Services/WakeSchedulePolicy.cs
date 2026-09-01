using PayBeat.App.Domain;

namespace PayBeat.App.Services;

/// <summary>
/// Pure wake-boundary policy for the floating widget.
/// Before today's work starts, wake at today's effective WorkStart.
/// After the work window (or on a fully determined off/PTO day), wake at the next
/// local midnight so rollover can resolve the new day's schedule before scheduling
/// that day's WorkStart.
/// </summary>
public static class WakeSchedulePolicy
{
    public static DateTime NextWakeBoundary(DateTime now, PayConfiguration config)
    {
        var today = DateOnly.FromDateTime(now);
        var status = config.ResolveDayStatus(today);
        var schedule = config.ResolveSchedule(today);
        var todayWorkStart = now.Date + schedule.WorkStart.ToTimeSpan();

        // Work/Makeup/Leave may still have live accrual later today. Do not skip
        // today's WorkStart by jumping directly to tomorrow.
        if (status is DayStatus.Work or DayStatus.MakeupWork or DayStatus.Leave
            && now < todayWorkStart)
        {
            return todayWorkStart;
        }

        // Once today's start has passed (or the day is Rest/Holiday/PTO), the next
        // correctness boundary while the main timer is stopped is local midnight.
        // The midnight refresh then resolves the new day's effective schedule and,
        // if still before work, schedules that same day's WorkStart.
        return now.Date.AddDays(1);
    }
}
