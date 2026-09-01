using PayBeat.App.Domain;
using PayBeat.App.Services;

namespace PayBeat.Tests;

/// <summary>
/// Deterministic regressions for the Round 3 correctness fixes.
/// These tests use fixed dates/times and assert observable production behavior.
/// </summary>
public class Round3RegressionTests : IDisposable
{
    private readonly string _tempDir;

    public Round3RegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PayBeatRound3_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private static PayConfiguration WakeConfig()
    {
        return new PayConfiguration
        {
            SalaryProfiles =
            [
                new SalaryProfile
                {
                    Mode = SalaryMode.Monthly,
                    MonthlyAmount = 6000m,
                    EffectiveFrom = new DateOnly(2000, 1, 1),
                },
            ],
            ScheduleProfiles =
            [
                new WorkScheduleProfile
                {
                    Id = "normal",
                    WorkStart = new TimeOnly(9, 0),
                    WorkEnd = new TimeOnly(18, 0),
                    EffectiveFrom = new DateOnly(2000, 1, 1),
                },
                new WorkScheduleProfile
                {
                    Id = "early",
                    WorkStart = new TimeOnly(7, 30),
                    WorkEnd = new TimeOnly(17, 0),
                    EffectiveFrom = new DateOnly(2026, 9, 2),
                },
            ],
            WeekPolicies =
            [
                WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2000, 1, 1)),
            ],
            Overrides = new Dictionary<DateOnly, CalendarOverride>(),
            Holidays = new HolidayCalendar([]),
        };
    }

    [Fact]
    public void BeforeWork_WakesAtTodaysWorkStart_NotTomorrow()
    {
        var config = WakeConfig();
        var now = new DateTime(2026, 9, 1, 7, 0, 0);

        var boundary = WakeSchedulePolicy.NextWakeBoundary(now, config);

        Assert.Equal(new DateTime(2026, 9, 1, 9, 0, 0), boundary);
    }

    [Fact]
    public void AfterWork_WakesAtNextMidnight()
    {
        var config = WakeConfig();
        var now = new DateTime(2026, 9, 1, 18, 30, 0);

        var boundary = WakeSchedulePolicy.NextWakeBoundary(now, config);

        Assert.Equal(new DateTime(2026, 9, 2, 0, 0, 0), boundary);
    }

    [Fact]
    public void Midnight_ResolvesNewSameDaySchedule_AndWakesAtEarlierStart()
    {
        var config = WakeConfig();
        var midnight = new DateTime(2026, 9, 2, 0, 0, 0);

        var boundary = WakeSchedulePolicy.NextWakeBoundary(midnight, config);

        Assert.Equal(new DateTime(2026, 9, 2, 7, 30, 0), boundary);
    }

    [Fact]
    public void Backfill_RepairsInternalGapBetweenExistingRecords()
    {
        var history = new HistoryService(Path.Combine(_tempDir, "history"));
        var config = TestConfig.Daily(230m);
        var first = new DateOnly(2026, 8, 27);
        var missing = new DateOnly(2026, 8, 28);
        var later = new DateOnly(2026, 8, 29);
        var today = new DateOnly(2026, 8, 30);

        history.RecordDay(first, new DayHistoryRecord
        {
            Date = first,
            Status = DayStatus.Work,
            DailyRate = 230m,
            TargetEarned = 230m,
            FinalEarned = 230m,
        });
        history.RecordDay(later, new DayHistoryRecord
        {
            Date = later,
            Status = DayStatus.Work,
            DailyRate = 230m,
            TargetEarned = 230m,
            FinalEarned = 230m,
        });

        HistoryBackfillService.Backfill(history, config, today);

        var loaded = history.Load(missing);
        Assert.NotNull(loaded);
        Assert.Contains(missing.ToString("yyyy-MM-dd"), loaded.Days.Keys);
    }

    [Fact]
    public void Backfill_DoesNotRewriteExistingInternalRecords()
    {
        var history = new HistoryService(Path.Combine(_tempDir, "history"));
        var config = TestConfig.Daily(230m);
        var first = new DateOnly(2026, 8, 27);
        var existing = new DateOnly(2026, 8, 28);
        var later = new DateOnly(2026, 8, 29);
        var today = new DateOnly(2026, 8, 30);

        history.RecordDay(first, new DayHistoryRecord
        {
            Date = first,
            Status = DayStatus.Work,
            DailyRate = 230m,
            TargetEarned = 230m,
            FinalEarned = 230m,
        });
        history.RecordDay(existing, new DayHistoryRecord
        {
            Date = existing,
            Status = DayStatus.Work,
            DailyRate = 999m,
            TargetEarned = 999m,
            FinalEarned = 999m,
        });
        history.RecordDay(later, new DayHistoryRecord
        {
            Date = later,
            Status = DayStatus.Work,
            DailyRate = 230m,
            TargetEarned = 230m,
            FinalEarned = 230m,
        });

        HistoryBackfillService.Backfill(history, config, today);

        var loaded = history.Load(existing);
        Assert.NotNull(loaded);
        Assert.Equal(999m, loaded.Days[existing.ToString("yyyy-MM-dd")].FinalEarned);
    }
}
