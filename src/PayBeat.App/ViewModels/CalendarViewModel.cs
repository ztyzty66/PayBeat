using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.Views;

namespace PayBeat.App.ViewModels;

/// <summary>One visible cell in the month grid.</summary>
public class CalendarDayVm
{
    public CalendarViewModel Owner { get; init; } = null!;
    public DateOnly Date { get; init; }
    public int DayNumber => Date.Day;
    public bool IsCurrentMonth { get; init; }
    public bool IsToday { get; init; }
    public DayStatus Status { get; init; }
    public bool HasLeave { get; init; }

    /// <summary>Whether the user set this day's status manually (calendar override).</summary>
    public bool HasOverride { get; init; }

    /// <summary>Tooltip for the manual-override badge.</summary>
    public string OverrideTip => LocalizationService.Get("Calendar.ManualBadge");
    public string StatusKey => Status switch { DayStatus.Work => "Calendar.Legend.Work", DayStatus.Rest => "Calendar.Legend.Rest", DayStatus.PublicHoliday => "Calendar.Legend.Holiday", DayStatus.MakeupWork => "Calendar.Legend.Makeup", DayStatus.PaidTimeOff => "Calendar.Legend.Pto", DayStatus.Leave => "Calendar.Legend.Leave", _ => "Calendar.Legend.Work" };
    public string Tag => Status switch { DayStatus.Work => "", DayStatus.Rest => LocalizationService.Get("Calendar.Legend.Rest"), DayStatus.PublicHoliday => LocalizationService.Get("Calendar.Status.Holiday"), DayStatus.MakeupWork => LocalizationService.Get("Calendar.Legend.Makeup"), DayStatus.PaidTimeOff => LocalizationService.Get("Calendar.Legend.Pto"), DayStatus.Leave => LocalizationService.Get("Calendar.Legend.Leave"), _ => "" };
    public bool ShowDot => IsCurrentMonth && Status == DayStatus.Work;
    public string TagBrushKey => Status switch { DayStatus.Rest => "TagRestBrush", DayStatus.PublicHoliday => "TagHolidayBrush", DayStatus.MakeupWork => "TagMakeupBrush", DayStatus.PaidTimeOff => "TagPtoBrush", DayStatus.Leave => "TagLeaveBrush", _ => "TagWorkBrush" };
    public string TagForegroundKey => Status switch { DayStatus.Rest => "RedBrush", DayStatus.PublicHoliday => "AmberBrush", DayStatus.MakeupWork => "BlueBrush", DayStatus.PaidTimeOff => "OrangeBrush", DayStatus.Leave => "PurpleBrush", _ => "GreenBrush" };
    public void OpenEditor() => Owner.EditDay(this);
}

/// <summary>
/// View model behind the calendar page. Uses shared <see cref="ConfigurationDraft"/> within SettingsWindow,
/// or reads from <see cref="ConfigurationStore"/> when standalone.
/// </summary>
public class CalendarViewModel : ViewModelBase
{
    private readonly ConfigurationStore _store;
    private readonly MainViewModel _mainVm;
    private readonly ConfigurationDraft? _draft;
    private PayConfiguration _config = null!;
    private DateOnly _displayMonth;
    private string _monthTitle = "";
    private IReadOnlyList<CalendarDayVm> _days = [];

    public CalendarViewModel(ConfigurationStore store, MainViewModel mainVm, ConfigurationDraft draft)
    {
        _store = store;
        _mainVm = mainVm;
        _draft = draft;
        // Live preview: re-render the grid whenever the shared draft changes (salary page week
        // edits, schedule manager activation, day-editor overrides). Draft and this view model
        // share the settings window's lifetime, so no unsubscribe is needed.
        draft.Changed += Rebuild;
        var today = DateOnly.FromDateTime(DateTime.Now);
        _displayMonth = new DateOnly(today.Year, today.Month, 1);
        Rebuild();
    }

    public CalendarViewModel(ConfigurationStore store, MainViewModel mainVm)
    {
        _store = store;
        _mainVm = mainVm;
        var today = DateOnly.FromDateTime(DateTime.Now);
        _displayMonth = new DateOnly(today.Year, today.Month, 1);
        Rebuild();
    }

    public IReadOnlyList<CalendarDayVm> Days { get => _days; private set => SetField(ref _days, value); }
    public string MonthTitle { get => _monthTitle; private set => SetField(ref _monthTitle, value); }
    public IReadOnlyList<CalendarDayVm> Grid => Days;

    /// <summary>Re-renders the month grid from the current store/draft state.</summary>
    public void Refresh() => Rebuild();

    public void PreviousMonth() { _displayMonth = _displayMonth.AddMonths(-1); Rebuild(); }
    public void NextMonth() { _displayMonth = _displayMonth.AddMonths(1); Rebuild(); }
    public void GoToToday() { var today = DateOnly.FromDateTime(DateTime.Now); _displayMonth = new DateOnly(today.Year, today.Month, 1); Rebuild(); }

    public void EditDay(CalendarDayVm day)
    {
        var currentSettings = _draft != null ? _draft.Base : _store.CurrentSettings;
        var existing = currentSettings.Overrides.TryGetValue(day.Date.ToString("yyyy-MM-dd"), out var ov) ? ov : null;
        var editor = new DayEditorWindow(day.Date, existing, _config);
        editor.ShowDialog();

        if (editor.Result is { } result)
        {
            var overrides = new Dictionary<string, CalendarOverride>(currentSettings.Overrides);
            var key = day.Date.ToString("yyyy-MM-dd");
            if (result.Clear) overrides.Remove(key); else overrides[key] = result.Override;
            if (_draft != null) _draft.Overrides = overrides; else _store.Commit(currentSettings with { Overrides = overrides });
        }
        Rebuild();
    }

    private void Rebuild()
    {
        _config = _draft != null ? _draft.BuildPreviewConfiguration(_store.PayData) : _store.CurrentConfiguration;
        var today = DateOnly.FromDateTime(DateTime.Now);
        MonthTitle = string.Format(LocalizationService.Get("Calendar.Title"), _displayMonth.Year, _displayMonth.Month);
        var cells = new List<CalendarDayVm>();
        var leading = ((int)_displayMonth.DayOfWeek + 6) % 7;
        var gridStart = _displayMonth.AddDays(-leading);
        for (var i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            var status = _config.ResolveDayStatus(date);
            cells.Add(new CalendarDayVm { Owner = this, Date = date, IsCurrentMonth = date.Month == _displayMonth.Month, IsToday = date == today, Status = status, HasLeave = _config.ResolveLeave(date) is not null, HasOverride = _config.Overrides.ContainsKey(date) });
        }
        Days = cells;
    }
}
