using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.Views;

namespace PayBeat.App.ViewModels;

/// <summary>One visible cell in the month grid.</summary>
public class CalendarDayVm
{
    /// <summary>Back-reference to the owning view model (for click handling).</summary>
    public CalendarViewModel Owner { get; init; } = null!;

    public DateOnly Date { get; init; }

    public int DayNumber => Date.Day;

    public bool IsCurrentMonth { get; init; }

    public bool IsToday { get; init; }

    public DayStatus Status { get; init; }

    public bool HasLeave { get; init; }

    public string StatusKey => Status switch
    {
        DayStatus.Work => "Calendar.Legend.Work",
        DayStatus.Rest => "Calendar.Legend.Rest",
        DayStatus.PublicHoliday => "Calendar.Legend.Holiday",
        DayStatus.MakeupWork => "Calendar.Legend.Makeup",
        DayStatus.PaidTimeOff => "Calendar.Legend.Pto",
        DayStatus.Leave => "Calendar.Legend.Leave",
        _ => "Calendar.Legend.Work",
    };

    /// <summary>Short tag text shown on the cell (empty for normal workdays — they show a dot instead).</summary>
    public string Tag => Status switch
    {
        DayStatus.Work => "",
        DayStatus.Rest => LocalizationService.Get("Calendar.Legend.Rest"),
        DayStatus.PublicHoliday => LocalizationService.Get("Calendar.Status.Holiday"),
        DayStatus.MakeupWork => LocalizationService.Get("Calendar.Legend.Makeup"),
        DayStatus.PaidTimeOff => LocalizationService.Get("Calendar.Legend.Pto"),
        DayStatus.Leave => LocalizationService.Get("Calendar.Legend.Leave"),
        _ => "",
    };

    public bool ShowDot => IsCurrentMonth && Status == DayStatus.Work;

    /// <summary>Brush resource key for the tag/background tint.</summary>
    public string TagBrushKey => Status switch
    {
        DayStatus.Rest => "TagRestBrush",
        DayStatus.PublicHoliday => "TagHolidayBrush",
        DayStatus.MakeupWork => "TagMakeupBrush",
        DayStatus.PaidTimeOff => "TagPtoBrush",
        DayStatus.Leave => "TagLeaveBrush",
        _ => "TagWorkBrush",
    };

    /// <summary>Foreground brush key for tag text.</summary>
    public string TagForegroundKey => Status switch
    {
        DayStatus.Rest => "RedBrush",
        DayStatus.PublicHoliday => "AmberBrush",
        DayStatus.MakeupWork => "BlueBrush",
        DayStatus.PaidTimeOff => "OrangeBrush",
        DayStatus.Leave => "PurpleBrush",
        _ => "GreenBrush",
    };

    /// <summary>Opens the day editor for this date.</summary>
    public void OpenEditor() => Owner.EditDay(this);
}

/// <summary>
/// View model behind the calendar page: builds a 6-week month grid from the effective
/// configuration (priority: override &gt; holiday &gt; week policy) and routes day clicks
/// to the day editor dialog.
/// </summary>
public class CalendarViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly MainViewModel _mainVm;
    private PayConfiguration _config = null!;
    private DateOnly _displayMonth;
    private string _monthTitle = "";
    private IReadOnlyList<CalendarDayVm> _days = [];

    public CalendarViewModel(SettingsService settingsService, MainViewModel mainVm)
    {
        _settingsService = settingsService;
        _mainVm = mainVm;
        var today = DateOnly.FromDateTime(DateTime.Now);
        _displayMonth = new DateOnly(today.Year, today.Month, 1);
        Rebuild();
    }

    /// <summary>Grid cells for the displayed month (6 weeks × 7 days).</summary>
    public IReadOnlyList<CalendarDayVm> Days
    {
        get => _days;
        private set => SetField(ref _days, value);
    }

    /// <summary>Header text, e.g. "2026年8月".</summary>
    public string MonthTitle
    {
        get => _monthTitle;
        private set => SetField(ref _monthTitle, value);
    }

    /// <summary>Grid command wrappers for XAML binding.</summary>
    public IReadOnlyList<CalendarDayVm> Grid => Days;

    /// <summary>Navigates to the previous month.</summary>
    public void PreviousMonth()
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        Rebuild();
    }

    /// <summary>Navigates to the next month.</summary>
    public void NextMonth()
    {
        _displayMonth = _displayMonth.AddMonths(1);
        Rebuild();
    }

    /// <summary>Jumps back to the current month.</summary>
    public void GoToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _displayMonth = new DateOnly(today.Year, today.Month, 1);
        Rebuild();
    }

    /// <summary>Opens the editor for a specific day (from a cell click).</summary>
    public void EditDay(CalendarDayVm day)
    {
        var current = Load();
        var existing = current.Overrides.TryGetValue(day.Date.ToString("yyyy-MM-dd"), out var ov) ? ov : null;
        var editor = new DayEditorWindow(day.Date, existing);
        editor.ShowDialog();

        if (editor.Result is { } result)
        {
            var overrides = new Dictionary<string, CalendarOverride>(current.Overrides);
            var key = day.Date.ToString("yyyy-MM-dd");
            if (result.Clear)
            {
                overrides.Remove(key);
            }
            else
            {
                overrides[key] = result.Override;
            }

            _settingsService.Save(current with { Overrides = overrides });
            _mainVm.ReloadSettings();
        }

        Rebuild();
    }

    private SalarySettings Load() => _settingsService.Load();

    private void Rebuild()
    {
        _config = new PayDataService(_settingsService, new HistoryService()).BuildConfiguration(Load());
        var today = DateOnly.FromDateTime(DateTime.Now);
        MonthTitle = string.Format(
            LocalizationService.Get("Calendar.Title"),
            _displayMonth.Year,
            _displayMonth.Month);

        var cells = new List<CalendarDayVm>();

        // First row starts on Monday; back-fill leading days from the previous month.
        var leading = ((int)_displayMonth.DayOfWeek + 6) % 7; // Monday=0
        var gridStart = _displayMonth.AddDays(-leading);

        for (var i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            var status = _config.ResolveDayStatus(date);
            cells.Add(new CalendarDayVm
            {
                Owner = this,
                Date = date,
                IsCurrentMonth = date.Month == _displayMonth.Month,
                IsToday = date == today,
                Status = status,
                HasLeave = _config.ResolveLeave(date) is not null,
            });
        }

        Days = cells;
    }
}
