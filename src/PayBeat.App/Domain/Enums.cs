namespace PayBeat.App.Domain;

/// <summary>How the user's salary is expressed.</summary>
public enum SalaryMode
{
    /// <summary>Fixed monthly amount; the daily rate is derived per calendar month.</summary>
    Monthly = 0,

    /// <summary>Fixed daily amount, identical every working day.</summary>
    Daily = 1,
}

/// <summary>Weekly rest policy preset.</summary>
public enum WorkWeekType
{
    /// <summary>Sat + Sun rest by default (editable rest days).</summary>
    DoubleRest = 0,

    /// <summary>Sun rest by default (editable rest days).</summary>
    SingleRest = 1,

    /// <summary>User picks every working weekday.</summary>
    Custom = 2,
}

/// <summary>Resolved status of a calendar day.</summary>
public enum DayStatus
{
    /// <summary>Normal scheduled working day (also used for user-forced work days).</summary>
    Work = 0,

    /// <summary>Scheduled rest day (weekend per policy or user-forced rest).</summary>
    Rest = 1,

    /// <summary>Official public holiday (paid, no accrual).</summary>
    PublicHoliday = 2,

    /// <summary>Official makeup workday (a weekend shifted into a working day).</summary>
    MakeupWork = 3,

    /// <summary>User-taken paid time off ("调休"): fully paid, no real-time accrual.</summary>
    PaidTimeOff = 4,

    /// <summary>User on leave (unpaid, partial or full day).</summary>
    Leave = 5,
}

/// <summary>Granularity of a leave record.</summary>
public enum LeaveKind
{
    /// <summary>Whole day off (00:00–24:00).</summary>
    FullDay = 0,

    /// <summary>Morning off: work start → lunch start (or → work end when no lunch).</summary>
    Morning = 1,

    /// <summary>Afternoon off: lunch end → work end (or → work start when no lunch).</summary>
    Afternoon = 2,

    /// <summary>Custom hour range within the day.</summary>
    Hours = 3,
}
