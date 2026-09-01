using PayBeat.App.Domain;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;

namespace PayBeat.Tests;

/// <summary>
/// Calendar natural-day rollover: the grid must re-render "today" automatically when the
/// system date changes (midnight, sleep across midnight), without user action, while leaving
/// a user-browsed historical month alone.
/// </summary>
public class CalendarRolloverTests : IDisposable
{
    private readonly string _tempDir;

    public CalendarRolloverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PayBeatRoll_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private CalendarViewModel CreateCalendar(out MainViewModel main, out ConfigurationStore store)
    {
        store = new ConfigurationStore(
            new SettingsService(_tempDir),
            new HistoryService(Path.Combine(_tempDir, "history")));
        main = new MainViewModel(store);
        return new CalendarViewModel(store, main);
    }

    private static CalendarDayVm? FindCell(CalendarViewModel vm, DateOnly date) =>
        vm.Days.FirstOrDefault(d => d.Date == date);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    // 1. 2026-08-29 → 2026-08-30: today moves automatically -------------------------------

    [Fact]
    public void DayRollover_TodayMovesToNewDate()
    {
        var cal = CreateCalendar(out _, out _);
        var t = Today;
        var next = t.AddDays(1);

        cal.ApplyToday(next);

        Assert.True(FindCell(cal, next)?.IsToday ?? false);
        var oldCell = FindCell(cal, t);
        Assert.True(oldCell is null || !oldCell.IsToday);
    }

    // 2. Selected/last-viewed 29 stays a normal cell; today must be 30 ----------------------

    [Fact]
    public void DayRollover_PreviousDayIsNotMarkedAsToday()
    {
        var cal = CreateCalendar(out _, out _);
        var t = Today;
        var next = t.AddDays(1);

        cal.ApplyToday(t);      // baseline: user was "on" the 29th
        cal.ApplyToday(next);   // midnight passes

        Assert.True(FindCell(cal, next)?.IsToday ?? false);
        // No persistent selection visual exists; the green border is Today-only, so the 29th
        // must not carry it after rollover.
        Assert.True(FindCell(cal, t)?.IsToday == false);
    }

    // 3. 08-31 → 09-01: current-month view auto-advances -------------------------------------

    [Fact]
    public void MonthRollover_AutoAdvancesDisplayedMonth()
    {
        var cal = CreateCalendar(out _, out _);
        var lastOfThisMonth = new DateOnly(Today.Year, Today.Month, DateTime.DaysInMonth(Today.Year, Today.Month));
        var firstOfNext = lastOfThisMonth.AddDays(1);

        cal.ApplyToday(lastOfThisMonth);     // baseline: end of the month
        cal.ApplyToday(firstOfNext);         // midnight crosses the month boundary

        Assert.Equal(new DateOnly(firstOfNext.Year, firstOfNext.Month, 1), cal.DisplayMonth);
        Assert.True(FindCell(cal, firstOfNext)?.IsToday ?? false);
    }

    // 4. Browsing a historical month: rollover must not yank the user back --------------------

    [Fact]
    public void DayRollover_WhileBrowsingHistory_StaysPut()
    {
        var cal = CreateCalendar(out _, out _);
        cal.PreviousMonth();
        cal.PreviousMonth();
        var browsedMonth = cal.DisplayMonth;

        cal.ApplyToday(Today.AddDays(1)); // midnight passes while user is two months back

        Assert.Equal(browsedMonth, cal.DisplayMonth); // not yanked to the current month
        // 今天 button still returns to the real current month at click time.
        cal.GoToToday();
        Assert.Equal(new DateOnly(Today.Year, Today.Month, 1), cal.DisplayMonth);
    }

    // 5. Sleep across midnight: the next refresh/date-check picks up the new date -------------

    [Fact]
    public void SleepAcrossMidnight_NextDateCheck_FiresDateChanged_AndCalendarRefreshes()
    {
        var cal = CreateCalendar(out var main, out _);
        var fired = 0;
        main.DateChanged += _ => fired++;

        var tomorrow = Today.AddDays(1);
        main.CheckDateRoll(tomorrow); // wake/resume path: date check after the gap

        Assert.Equal(1, fired);
        Assert.Equal(new DateOnly(tomorrow.Year, tomorrow.Month, 1), cal.DisplayMonth);
        Assert.True(FindCell(cal, tomorrow)?.IsToday ?? false);
    }

    [Fact]
    public void DateCheck_SameDate_DoesNotFire()
    {
        var cal = CreateCalendar(out var main, out _);
        var fired = 0;
        main.DateChanged += _ => fired++;

        main.CheckDateRoll(Today);

        Assert.Equal(0, fired);
    }

    // Grid cells reflect the tracked today (not a cached construction-time value) -------------

    [Fact]
    public void Rebuild_UsesTrackedToday_NotCachedDate()
    {
        var cal = CreateCalendar(out _, out _);
        var t = Today;
        var next = t.AddDays(1);

        cal.ApplyToday(next);
        cal.PreviousMonth(); // force a rebuild of another month
        cal.NextMonth();     // back

        if (cal.DisplayMonth == new DateOnly(next.Year, next.Month, 1))
        {
            Assert.True(FindCell(cal, next)?.IsToday ?? false);
        }
        Assert.True(FindCell(cal, t)?.IsToday == false);
    }
}
