namespace PayBeat.App.Domain;

/// <summary>Monthly aggregate result produced by <see cref="SalaryEngine.ComputeMonth"/>.</summary>
public sealed record MonthSummary
{
    public required DateOnly Month { get; init; }

    /// <summary>Planned paid workdays of the month (leave/PTO days included, so the daily rate never re-averages).</summary>
    public required int PlannedWorkdays { get; init; }

    /// <summary>Standard monthly amount (monthly profile amount, or daily × planned days in daily mode).</summary>
    public required decimal StandardMonthly { get; init; }

    /// <summary>Expected total for the month after leave deductions (调休 does not reduce it).</summary>
    public required decimal MonthTarget { get; init; }

    /// <summary>Earned so far: past days at their final values + today's live accrual.</summary>
    public required decimal MonthEarned { get; init; }

    /// <summary>Planned workdays strictly before today (the "15" in "15/26 days" progress).</summary>
    public required int PassedWorkdays { get; init; }

    /// <summary>Paid time off days in the month.</summary>
    public required int PtoDays { get; init; }

    /// <summary>Total effective leave hours (lunch never counted).</summary>
    public required decimal LeaveHours { get; init; }
}
