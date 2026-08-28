using PayBeat.App.Domain;
using PayBeat.App.Helpers;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.Views;

namespace PayBeat.App.ViewModels;

/// <summary>
/// Primary view model for the floating widget. Owns the refresh timer and drives all displayed
/// state through <see cref="SalaryEngine"/>: today's live earnings/progress/phase, month
/// aggregates (cached; recomputed on day/config changes only — never per tick), and history
/// snapshot bookkeeping. Exposes commands for display mode switching, settings, and details.
/// </summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly PayDataService _payData;
    private readonly DispatcherTimer _timer;
    private DateOnly _notifiedDate;
    private decimal _nextMilestoneThreshold;
    private bool _endOfDayReminderSent;
    private bool _notificationsSuspended;
    private DispatcherTimer? _wakeTimer;

    private SalarySettings _settings;
    private PayConfiguration _config;

    // Cached month aggregate: the expensive whole-month pass runs on day/config change only.
    private DateOnly _cachedMonth;
    private MonthSummary _monthSummary = null!;
    private decimal _pastDaysEarned;

    // Live state exposed to views.
    private DayProgress _today = null!;
    private string _statusKey = "Status.Working";
    private string _statusDetail = "";

    /// <summary>
    /// Initializes a new instance of <see cref="MainViewModel"/>, loads settings, starts the refresh timer,
    /// and performs an immediate <see cref="Refresh"/> to populate the initial display.
    /// </summary>
    /// <param name="settingsService">Service used to load and save salary settings.</param>
    public MainViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _payData = new PayDataService(settingsService, new HistoryService());
        _settings = _settingsService.Load();
        _config = _payData.BuildConfiguration(_settings);
        _notifiedDate = DateOnly.FromDateTime(DateTime.Now);
        _nextMilestoneThreshold = _settings.MilestoneAmount;

        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenAboutCommand = new RelayCommand(OpenAbout);
        OpenDetailCommand = new RelayCommand(OpenDetail);
        ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
        SetNormalModeCommand = new RelayCommand(() => SetDisplayMode(DisplayMode.Normal));
        SetMiniModeCommand = new RelayCommand(() => SetDisplayMode(DisplayMode.Mini));
        SetNoneModeCommand = new RelayCommand(() => SetDisplayMode(DisplayMode.None));
        SetFlexModeCommand = new RelayCommand(() => SetDisplayMode(DisplayMode.Flex));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.RefreshInterval) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        RebuildMonthCache(DateTime.Now);
        Refresh();
    }

    /// <summary>Raised when hotkey settings change so <c>App</c> can re-register the global hotkey.</summary>
    public event Action? HotkeySettingsChanged;

    /// <summary>Raised when a milestone or end-of-day reminder should be shown as a tray notification.</summary>
    public event Action<string, string>? NotificationRequested;

    /// <summary>Whether the window should stay above all other windows.</summary>
    public bool AlwaysOnTop => _settings.AlwaysOnTop;

    /// <summary>Currency symbol shown before the earnings amount.</summary>
    public string Currency => _settings.Currency;

    /// <summary>Currently active display mode; drives the view template selection in <c>MainWindow</c>.</summary>
    public DisplayMode DisplayMode => _settings.DisplayMode;

    /// <summary>Amount earned so far today (Decimal).</summary>
    public decimal Earned => _today.Earned;

    /// <summary>Earned amount formatted as <c>{Currency}{Amount:N2}</c> (e.g. <c>¥123.45</c>).</summary>
    public string EarnedFormatted => $"{_settings.Currency}{Earned:N2}";

    /// <summary>Today's target (daily rate minus leave deduction; full rate on PTO days).</summary>
    public decimal TargetToday => _today.Computation.TargetEarned;

    /// <summary>Today's target formatted as <c>{Currency}{Amount:N2}</c>.</summary>
    public string TargetTodayFormatted => $"{_settings.Currency}{TargetToday:N2}";

    /// <summary>Standard daily rate shown in dashboards.</summary>
    public decimal DailyRate => _today.Computation.DailyRate;

    /// <summary>Daily rate formatted.</summary>
    public string DailyRateFormatted => $"{_settings.Currency}{DailyRate:N2}";

    /// <summary>Workday completion fraction in [0.0, 1.0], bound to progress bars.</summary>
    public double Progress => _today.Progress;

    /// <summary>Progress formatted as a short percentage, e.g. <c>54.8%</c>.</summary>
    public string ProgressText => $"{Progress * 100d:0.0}%";

    /// <summary>Effective work seconds completed today.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(_today.WorkedSeconds);

    /// <summary>Elapsed work time formatted compactly, e.g. <c>4h 23m</c>.</summary>
    public string ElapsedFormattedShort => FormatDuration(_today.WorkedSeconds);

    /// <summary>Legacy full format (<c>hh:mm:ss</c>) for views that still use it.</summary>
    public string ElapsedFormatted => Elapsed.ToString(@"hh\:mm\:ss");

    /// <summary>Wall-clock time remaining until work end (zero after end).</summary>
    public TimeSpan Remaining => TimeSpan.FromSeconds(_today.RemainingSeconds);

    /// <summary>Remaining time formatted compactly, e.g. <c>3h 37m</c>.</summary>
    public string RemainingFormattedShort => FormatDuration(_today.RemainingSeconds);

    /// <summary>Legacy full format (<c>hh:mm:ss</c>).</summary>
    public string RemainingFormatted => Remaining.ToString(@"hh\:mm\:ss");

    /// <summary>Phase of the current day.</summary>
    public DayPhase Phase => _today.Phase;

    /// <summary>Localization key describing today's status (working / lunch / done / rest / PTO / leave...).</summary>
    public string StatusKey
    {
        get => _statusKey;
        private set
        {
            if (SetField(ref _statusKey, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Localized status line shown on the widget (e.g. "午休中").</summary>
    public string StatusText => LocalizationService.Get(StatusKey);

    /// <summary>Secondary status line (e.g. "13:00 继续计薪" or "距上班 25 分钟").</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetField(ref _statusDetail, value);
    }

    /// <summary>Work start of the effective schedule (display).</summary>
    public TimeOnly WorkStart => _today.Computation.Schedule.WorkStart;

    /// <summary>Work end of the effective schedule (display).</summary>
    public TimeOnly WorkEnd => _today.Computation.Schedule.WorkEnd;

    /// <summary>Work window text, e.g. <c>08:00 – 17:30</c>.</summary>
    public string WorkWindowText =>
        $"{WorkStart:HH:mm} – {WorkEnd:HH:mm}";

    /// <summary>Lunch window text when enabled, e.g. <c>12:00 – 13:30</c>; empty otherwise.</summary>
    public string LunchWindowText =>
        _today.Computation.Schedule.LunchSpan() is { } lunch ? $"{lunch.Start:HH:mm} – {lunch.End:HH:mm}" : "";

    /// <summary>True when today's schedule deducts lunch.</summary>
    public bool HasLunch => LunchWindowText.Length > 0;

    // ── month aggregates ────────────────────────────────────────────────────────────────

    /// <summary>Standard monthly amount (monthly profile amount, or daily × planned days).</summary>
    public decimal StandardMonthly => _monthSummary.StandardMonthly;

    /// <summary>Formatted standard monthly amount.</summary>
    public string StandardMonthlyFormatted => $"{_settings.Currency}{StandardMonthly:N2}";

    /// <summary>Expected total for the month after leave deductions.</summary>
    public decimal MonthTarget => _monthSummary.MonthTarget;

    /// <summary>Formatted expected total.</summary>
    public string MonthTargetFormatted => $"{_settings.Currency}{MonthTarget:N2}";

    /// <summary>Earned so far this month (past days final + today live).</summary>
    public decimal MonthEarned => _pastDaysEarned + Earned;

    /// <summary>Formatted month earnings.</summary>
    public string MonthEarnedFormatted => $"{_settings.Currency}{MonthEarned:N2}";

    /// <summary>Month-earned ÷ month-target in [0,1].</summary>
    public double MonthProgress => MonthTarget > 0 ? Math.Clamp((double)(MonthEarned / MonthTarget), 0d, 1d) : 0d;

    /// <summary>Month progress formatted, e.g. <c>57.1%</c>.</summary>
    public string MonthProgressText => $"{MonthProgress * 100d:0.0}%";

    /// <summary>Workday progress text, e.g. <c>15 / 26</c>.</summary>
    public string WorkdaysText => $"{_monthSummary.PassedWorkdays} / {_monthSummary.PlannedWorkdays}";

    /// <summary>Leave hours text, e.g. <c>8.0</c>.</summary>
    public string LeaveHoursText => $"{_monthSummary.LeaveHours:0.#}";

    /// <summary>PTO days text.</summary>
    public string PtoDaysText => $"{_monthSummary.PtoDays}";

    /// <summary>Name of the schedule effective today.</summary>
    public string ScheduleName
    {
        get
        {
            var s = _today.Computation.Schedule;
            if (!string.IsNullOrWhiteSpace(s.Name))
            {
                return s.Name;
            }
            var current = _config.ScheduleProfiles.FirstOrDefault(p => p.Id == s.Id);
            return current?.Name ?? LocalizationService.Get("Salary.DefaultScheduleName");
        }
    }

    // ── convenience flags for menus ─────────────────────────────────────────────────────

    /// <summary>Convenience flag bound to the display mode menu checkboxes.</summary>
    public bool IsFlexMode => _settings.DisplayMode == DisplayMode.Flex;

    /// <summary>Convenience flag bound to the display mode menu checkboxes.</summary>
    public bool IsMiniMode => _settings.DisplayMode == DisplayMode.Mini;

    /// <summary>Convenience flag bound to the display mode menu checkboxes.</summary>
    public bool IsNoneMode => _settings.DisplayMode == DisplayMode.None;

    /// <summary>Convenience flag bound to the display mode menu checkboxes.</summary>
    public bool IsNormalMode => _settings.DisplayMode == DisplayMode.Normal;

    /// <summary>Window opacity at idle (not hovered).</summary>
    public double Opacity => _settings.Opacity;

    /// <summary>Opens the about window, or activates it if already open.</summary>
    public ICommand OpenAboutCommand
    {
        get;
    }

    /// <summary>Opens the settings window, or activates it if already open.</summary>
    public ICommand OpenSettingsCommand
    {
        get;
    }

    /// <summary>Opens the detail (today/month dashboard) window, or activates it if already open.</summary>
    public ICommand OpenDetailCommand
    {
        get;
    }

    /// <summary>Shuts down the application.</summary>
    public ICommand ExitCommand
    {
        get;
    }

    /// <summary>Switches the widget to <see cref="DisplayMode.Flex"/> and saves the change.</summary>
    public ICommand SetFlexModeCommand
    {
        get;
    }

    /// <summary>Switches the widget to <see cref="DisplayMode.Mini"/> and saves the change.</summary>
    public ICommand SetMiniModeCommand
    {
        get;
    }

    /// <summary>Switches the widget to <see cref="DisplayMode.None"/> and saves the change.</summary>
    public ICommand SetNoneModeCommand
    {
        get;
    }

    /// <summary>Switches the widget to <see cref="DisplayMode.Normal"/> and saves the change.</summary>
    public ICommand SetNormalModeCommand
    {
        get;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _wakeTimer?.Stop();
        _timer.Stop();
    }

    /// <summary>Resumes end-of-day/milestone tray notifications after a prior <see cref="SuspendNotifications"/> call.</summary>
    public void ResumeNotifications() => _notificationsSuspended = false;

    /// <summary>Suppresses end-of-day/milestone tray notifications while the widget is hidden.</summary>
    public void SuspendNotifications() => _notificationsSuspended = true;

    /// <summary>
    /// Reloads settings from disk, rebuilds the configuration and month cache, and notifies all
    /// bound properties. Called by <see cref="SettingsViewModel"/> after the user saves changes,
    /// and by the first-run window.
    /// Also raises <see cref="HotkeySettingsChanged"/> so <c>App</c> can re-register the hotkey.
    /// </summary>
    public void ReloadSettings()
    {
        _settings = _settingsService.Load();
        _config = _payData.BuildConfiguration(_settings);
        OnPropertyChanged(nameof(DisplayMode));
        OnPropertyChanged(nameof(AlwaysOnTop));
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(Currency));
        OnPropertyChanged(nameof(IsNormalMode));
        OnPropertyChanged(nameof(IsMiniMode));
        OnPropertyChanged(nameof(IsNoneMode));
        OnPropertyChanged(nameof(IsFlexMode));
        _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshInterval);
        _wakeTimer?.Stop();
        _wakeTimer = null;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
        _nextMilestoneThreshold = NextMilestoneThresholdAbove(Earned, _settings.MilestoneAmount);
        _endOfDayReminderSent = false;
        RebuildMonthCache(DateTime.Now);
        HotkeySettingsChanged?.Invoke();
        Refresh();
    }

    /// <summary>Raises <see cref="NotificationRequested"/> with a localized "update available" message.</summary>
    public void NotifyUpdateAvailable(string version) =>
        NotificationRequested?.Invoke(
            LocalizationService.Get("Notification.UpdateAvailableTitle"),
            string.Format(LocalizationService.Get("Notification.UpdateAvailableBody"), version));

    private void OpenAbout()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is AboutWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        var win = new AboutWindow(_settingsService);
        ApplyTopmostIfNeeded(win);
        win.Show();
    }

    private void OpenSettings()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is SettingsWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        var win = new SettingsWindow
        {
            DataContext = new SettingsViewModel(_settingsService, this)
        };
        ApplyTopmostIfNeeded(win);
        win.Show();
    }

    private void OpenDetail()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is DetailWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        var win = new DetailWindow(_payData, this);
        ApplyTopmostIfNeeded(win);
        win.Show();
    }

    // MainWindow stays pinned to HWND_TOPMOST while AlwaysOnTop is on (see TopmostHelper),
    // which would otherwise bury this dialog behind it in fullscreen Flex mode.
    private static void ApplyTopmostIfNeeded(Window win)
    {
        if (Application.Current.MainWindow is { Topmost: true } mainWindow)
        {
            win.Owner = mainWindow;
            win.Topmost = true;
        }
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        // Day rollover: snapshot yesterday into history, finalize the previous month.
        if (today != _notifiedDate)
        {
            OnDayRollover(_notifiedDate, today);
            _notifiedDate = today;
            _nextMilestoneThreshold = _settings.MilestoneAmount;
            _endOfDayReminderSent = false;
        }

        if (today.Month != _cachedMonth.Month || today.Year != _cachedMonth.Year)
        {
            RebuildMonthCache(now);
        }

        _today = SalaryEngine.ComputeDayAt(_config, today, TimeOnly.FromDateTime(now));
        UpdateStatusTexts(now);

        OnPropertyChanged(nameof(Earned));
        OnPropertyChanged(nameof(EarnedFormatted));
        OnPropertyChanged(nameof(TargetToday));
        OnPropertyChanged(nameof(TargetTodayFormatted));
        OnPropertyChanged(nameof(DailyRate));
        OnPropertyChanged(nameof(DailyRateFormatted));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(Elapsed));
        OnPropertyChanged(nameof(ElapsedFormatted));
        OnPropertyChanged(nameof(ElapsedFormattedShort));
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(RemainingFormatted));
        OnPropertyChanged(nameof(RemainingFormattedShort));
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(WorkStart));
        OnPropertyChanged(nameof(WorkEnd));
        OnPropertyChanged(nameof(WorkWindowText));
        OnPropertyChanged(nameof(LunchWindowText));
        OnPropertyChanged(nameof(HasLunch));
        OnPropertyChanged(nameof(ScheduleName));
        OnPropertyChanged(nameof(MonthEarned));
        OnPropertyChanged(nameof(MonthEarnedFormatted));
        OnPropertyChanged(nameof(MonthProgress));
        OnPropertyChanged(nameof(MonthProgressText));

        CheckNotifications(now);

        var schedule = _today.Computation.Schedule;
        var current = TimeOnly.FromDateTime(now);
        if (current <= schedule.WorkStart || current >= schedule.WorkEnd)
        {
            _timer.Stop();
            ScheduleWakeTimer(now);
        }
    }

    private void UpdateStatusTexts(DateTime now)
    {
        var schedule = _today.Computation.Schedule;

        switch (_today.Phase)
        {
            case DayPhase.OffDay:
                StatusKey = _today.Computation.Status == DayStatus.PublicHoliday
                    ? "Status.Holiday"
                    : "Status.Rest";
                StatusDetail = string.Empty;
                break;
            case DayPhase.PaidTimeOff:
                StatusKey = "Status.Pto";
                StatusDetail = string.Empty;
                break;
            case DayPhase.BeforeWork:
                StatusKey = "Status.BeforeWork";
                var minutes = (int)Math.Round((SecondsOf(schedule.WorkStart) - SecondsOf(TimeOnly.FromDateTime(now))) / 60d);
                StatusDetail = string.Format(LocalizationService.Get("Status.UntilStart"), Math.Max(minutes, 0));
                break;
            case DayPhase.Lunch:
                StatusKey = "Status.Lunch";
                StatusDetail = string.Format(
                    LocalizationService.Get("Status.ResumeAt"),
                    schedule.LunchSpan()?.End.ToString("HH:mm") ?? string.Empty);
                break;
            case DayPhase.AfterWork:
                StatusKey = _today.Computation.Status == DayStatus.Leave
                    ? "Status.Leave"
                    : "Status.Done";
                StatusDetail = string.Empty;
                break;
            case DayPhase.Working:
                StatusKey = _today.Computation.Status == DayStatus.Leave
                    ? "Status.Leave"
                    : "Status.Working";
                StatusDetail = string.Empty;
                break;
            default:
                StatusKey = "Status.Working";
                StatusDetail = string.Empty;
                break;
        }
    }

    /// <summary>Day rollover bookkeeping: snapshot the completed day, finalize the month if it ended.</summary>
    private void OnDayRollover(DateOnly completedDay, DateOnly newDay)
    {
        try
        {
            var day = SalaryEngine.ComputeDay(_config, completedDay);
            _payData.SnapshotDay(day, _config, _config.PlannedWorkdays(completedDay));

            var lastOfCompletedMonth = new DateOnly(
                completedDay.Year, completedDay.Month,
                DateTime.DaysInMonth(completedDay.Year, completedDay.Month));
            if (completedDay == lastOfCompletedMonth)
            {
                FinalizeMonth(completedDay);
            }
        }
        catch
        {
            // History bookkeeping must never crash the live widget.
        }

        RebuildMonthCache(DateTime.Now);
    }

    private void FinalizeMonth(DateOnly lastDay)
    {
        var summary = SalaryEngine.ComputeMonth(_config, lastDay, lastDay.AddDays(1), new TimeOnly(0, 0));
        var history = _payData.History.Load(lastDay) ?? new MonthHistory
        {
            Month = $"{lastDay.Year:D4}-{lastDay.Month:D2}",
        };
        _payData.History.FinalizeMonth(lastDay, history with
        {
            StandardMonthlySnapshot = summary.StandardMonthly,
            MonthTargetSnapshot = summary.MonthTarget,
            MonthEarnedSnapshot = summary.MonthEarned,
            PlannedWorkdays = summary.PlannedWorkdays,
            PtoDays = summary.PtoDays,
            LeaveHours = summary.LeaveHours,
            WorkWeekTypeSnapshot = _config.ResolveWeekPolicy(lastDay).Type.ToString(),
        });
    }

    /// <summary>
    /// Recomputes the whole-month aggregates for the month containing <paramref name="now"/>.
    /// Days strictly before today contribute their final earned values (cached in
    /// <see cref="_pastDaysEarned"/>); today's live accrual is added per tick on top.
    /// </summary>
    private void RebuildMonthCache(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        _cachedMonth = new DateOnly(today.Year, today.Month, 1);
        _monthSummary = SalaryEngine.ComputeMonth(_config, _cachedMonth, today, TimeOnly.FromDateTime(now));

        _pastDaysEarned = 0m;
        foreach (var date in PayConfiguration.EachDay(_cachedMonth))
        {
            if (date >= today)
            {
                break;
            }
            _pastDaysEarned += SalaryEngine.ComputeDay(_config, date).FinalEarned;
        }

        OnPropertyChanged(nameof(StandardMonthly));
        OnPropertyChanged(nameof(StandardMonthlyFormatted));
        OnPropertyChanged(nameof(MonthTarget));
        OnPropertyChanged(nameof(MonthTargetFormatted));
        OnPropertyChanged(nameof(WorkdaysText));
        OnPropertyChanged(nameof(LeaveHoursText));
        OnPropertyChanged(nameof(PtoDaysText));
    }

    /// <summary>Smallest multiple of <paramref name="milestoneAmount"/> that is strictly greater than <paramref name="earned"/>.</summary>
    private static decimal NextMilestoneThresholdAbove(decimal earned, decimal milestoneAmount)
    {
        if (milestoneAmount <= 0)
        {
            return milestoneAmount;
        }

        return (Math.Floor(earned / milestoneAmount) + 1) * milestoneAmount;
    }

    private void CheckNotifications(DateTime now)
    {
        if (_notificationsSuspended || !_today.Computation.IsPaidDay)
        {
            return;
        }

        if (_settings.EnableMilestoneNotifications && _settings.MilestoneAmount > 0)
        {
            while (Earned >= _nextMilestoneThreshold)
            {
                NotificationRequested?.Invoke(
                    LocalizationService.Get("Notification.MilestoneTitle"),
                    string.Format(LocalizationService.Get("Notification.MilestoneBody"), $"{_settings.Currency}{_nextMilestoneThreshold:N2}"));
                _nextMilestoneThreshold += _settings.MilestoneAmount;
            }
        }

        if (_settings.EnableEndOfDayReminder && !_endOfDayReminderSent
            && Remaining > TimeSpan.Zero && Remaining <= TimeSpan.FromMinutes(_settings.EndOfDayReminderMinutes))
        {
            _endOfDayReminderSent = true;
            NotificationRequested?.Invoke(
                LocalizationService.Get("Notification.EndOfDayTitle"),
                string.Format(LocalizationService.Get("Notification.EndOfDayBody"), _settings.EndOfDayReminderMinutes));
        }
    }

    private void ScheduleWakeTimer(DateTime now)
    {
        _wakeTimer?.Stop();

        var current = TimeOnly.FromDateTime(now);
        var schedule = _today.Computation.Schedule;
        var nextStart = current < schedule.WorkStart
            ? now.Date + schedule.WorkStart.ToTimeSpan()
            : now.Date.AddDays(1) + schedule.WorkStart.ToTimeSpan();

        var delay = nextStart - now;
        if (delay > TimeSpan.FromDays(1))
        {
            delay = TimeSpan.FromDays(1); // re-check daily at minimum (weekends/holidays)
        }
        _wakeTimer = new DispatcherTimer { Interval = delay };
        _wakeTimer.Tick += (_, _) =>
        {
            _wakeTimer!.Stop();
            _wakeTimer = null;
            _timer.Start();
            Refresh();
        };
        _wakeTimer.Start();
    }

    private void SetDisplayMode(DisplayMode mode)
    {
        var current = _settingsService.Load();
        if (current.DisplayMode == mode)
        {
            return;
        }

        _settingsService.Save(current with
        {
            DisplayMode = mode
        });
        ReloadSettings();
    }

    private static string FormatDuration(double seconds)
    {
        var total = (long)Math.Max(seconds, 0);
        var h = total / 3600;
        var m = (total % 3600) / 60;
        return $"{h}h {m:D2}m";
    }

    private static double SecondsOf(TimeOnly t) => t.Hour * 3600d + t.Minute * 60d;
}
