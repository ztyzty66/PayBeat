namespace PayBeat.App.Domain;

/// <summary>Real-time phase of the current day, driving widget status text.</summary>
public enum DayPhase
{
    /// <summary>Date is a rest day / public holiday (no accrual).</summary>
    OffDay = 0,

    /// <summary>Paid time off ("调休") — fully paid, no real-time accrual.</summary>
    PaidTimeOff = 1,

    /// <summary>Before work start.</summary>
    BeforeWork = 2,

    /// <summary>Inside the lunch break.</summary>
    Lunch = 3,

    /// <summary>Working (accruing).</summary>
    Working = 4,

    /// <summary>After work end (capped).</summary>
    AfterWork = 5,
}

/// <summary>Final (end-of-day) computation for one calendar date. Immutable and replayable.</summary>
public sealed record DayComputation
{
    public required DateOnly Date { get; init; }

    public required DayStatus Status { get; init; }

    /// <summary>Schedule effective on this date.</summary>
    public required WorkScheduleProfile Schedule { get; init; }

    /// <summary>Standard daily rate for this date (Decimal).</summary>
    public required decimal DailyRate { get; init; }

    /// <summary>Total effective (paid) seconds of the day after lunch deduction; > 0 for paid days.</summary>
    public required double TotalEffectiveSeconds { get; init; }

    /// <summary>Effective leave seconds actually deducted (leave ∩ paid spans; lunch never counted). Only set for Leave days.</summary>
    public required double LeaveSeconds { get; init; }

    public bool IsPaidDay => Status is DayStatus.Work or DayStatus.MakeupWork;

    /// <summary>Leave deduction = DailyRate × effective leave seconds ÷ standard effective seconds.</summary>
    public decimal LeaveDeduction => Status == DayStatus.Leave && TotalEffectiveSeconds > 0
        ? DailyRate * ((decimal)LeaveSeconds / (decimal)TotalEffectiveSeconds)
        : 0m;

    /// <summary>
    /// Day target. Paid and leave days earn the daily rate minus leave deduction (never negative);
    /// paid time off pays the full daily rate; rest and holidays pay nothing.
    /// </summary>
    public decimal TargetEarned => Status switch
    {
        DayStatus.Work or DayStatus.MakeupWork or DayStatus.Leave =>
            TotalEffectiveSeconds > 0
                ? Math.Max(0m, DailyRate * (1m - (decimal)LeaveSeconds / (decimal)TotalEffectiveSeconds))
                : 0m,
        DayStatus.PaidTimeOff => DailyRate,
        _ => 0m,
    };

    /// <summary>End-of-day earned equals the day target (accrual caps exactly there).</summary>
    public decimal FinalEarned => TargetEarned;
}

/// <summary>Point-in-time progress for one date as of an evaluation time.</summary>
public sealed record DayProgress
{
    public required DayComputation Computation { get; init; }

    public required DayPhase Phase { get; init; }

    /// <summary>Earned as of the evaluation time (Decimal, full precision).</summary>
    public required decimal Earned { get; init; }

    /// <summary>Effective work seconds completed as of the evaluation time (lunch and leave excluded).</summary>
    public required double WorkedSeconds { get; init; }

    /// <summary>Wall-clock seconds until work end, clamped to [0, work window].</summary>
    public required double RemainingSeconds { get; init; }

    /// <summary>Earned ÷ target, clamped to [0,1].</summary>
    public double Progress =>
        Computation.TargetEarned > 0 ? Math.Clamp((double)(Earned / Computation.TargetEarned), 0d, 1d) : 0d;
}
