using PayBeat.App.Domain;
using PayBeat.App.Helpers;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.Views;

namespace PayBeat.App.ViewModels;

/// <summary>
/// Primary view model for the floating widget. Owns the refresh timer and drives all displayed
/// state through <see cref="SalaryEngine"/>. Reads configuration from <see cref="ConfigurationStore"/>
/// — never from disk directly.
/// </summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ConfigurationStore _store;
    private readonly DispatcherTimer _timer;
    private DateOnly _notifiedDate;
    private decimal _nextMilestoneThreshold;
    private bool _endOfDayReminderSent;
    private bool _notificationsSuspended;
    private DispatcherTimer? _wakeTimer;

    private SalarySettings _settings;
    private PayConfiguration _config;

    private DateOnly _cachedMonth;
    private MonthSummary _monthSummary = null!;
    private decimal _pastDaysEarned;

    private DayProgress _today = null!;
    private string _statusKey = "Status.Working";
    private string _statusDetail = "";

    public MainViewModel(ConfigurationStore store)
    {
        _store = store;
        _settings = store.CurrentSettings;
        _config = store.CurrentConfiguration;
        _notifiedDate = DateOnly.FromDateTime(DateTime.Now);
        _nextMilestoneThreshold = _settings.MilestoneAmount;

        _store.ConfigurationChanged += OnConfigurationChanged;

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

    public event Action? HotkeySettingsChanged;
    public event Action<string, string>? NotificationRequested;

    public bool AlwaysOnTop => _settings.AlwaysOnTop;
    public string Currency => _settings.Currency;
    public DisplayMode DisplayMode => _settings.DisplayMode;
    public decimal Earned => _today.Earned;
    public string EarnedFormatted => $"{_settings.Currency}{Earned:N2}";
    public decimal TargetToday => _today.Computation.TargetEarned;
    public string TargetTodayFormatted => $"{_settings.Currency}{TargetToday:N2}";
    public decimal DailyRate => _today.Computation.DailyRate;
    public string DailyRateFormatted => $"{_settings.Currency}{DailyRate:N2}";
    public double Progress => _today.Progress;
    public string ProgressText => $"{Progress * 100d:0.0}%";
    public TimeSpan Elapsed => TimeSpan.FromSeconds(_today.WorkedSeconds);
    public string ElapsedFormattedShort => FormatDuration(_today.WorkedSeconds);
    public string ElapsedFormatted => Elapsed.ToString(@"hh\:mm\:ss");
    public TimeSpan Remaining => TimeSpan.FromSeconds(_today.RemainingSeconds);
    public string RemainingFormattedShort => FormatDuration(_today.RemainingSeconds);
    public string RemainingFormatted => Remaining.ToString(@"hh\:mm\:ss");
    public DayPhase Phase => _today.Phase;
    public bool HasLunch => LunchWindowText.Length > 0;
    public bool IsRestDay => _today.Phase == DayPhase.OffDay;
    public string RestDayMessage => LocalizationService.Get("Status.RestDayMessage");

    public string StatusKey
    {
        get => _statusKey;
        private set { if (SetField(ref _statusKey, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText => LocalizationService.Get(StatusKey);

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetField(ref _statusDetail, value);
    }

    public TimeOnly WorkStart => _today.Computation.Schedule.WorkStart;
    public TimeOnly WorkEnd => _today.Computation.Schedule.WorkEnd;
    public string WorkWindowText => $"{WorkStart:HH:mm} – {WorkEnd:HH:mm}";
    public string LunchWindowText =>
        _today.Computation.Schedule.LunchSpan() is { } lunch ? $"{lunch.Start:HH:mm} – {lunch.End:HH:mm}" : "";

    public decimal StandardMonthly => _monthSummary.StandardMonthly;
    public string StandardMonthlyFormatted => $"{_settings.Currency}{StandardMonthly:N2}";
    public decimal MonthTarget => _monthSummary.MonthTarget;
    public string MonthTargetFormatted => $"{_settings.Currency}{MonthTarget:N2}";
    public decimal MonthEarned => _pastDaysEarned + Earned;
    public string MonthEarnedFormatted => $"{_settings.Currency}{MonthEarned:N2}";
    public double MonthProgress => MonthTarget > 0 ? Math.Clamp((double)(MonthEarned / MonthTarget), 0d, 1d) : 0d;
    public string MonthProgressText => $"{MonthProgress * 100d:0.0}%";
    public string WorkdaysText => $"{_monthSummary.PassedWorkdays} / {_monthSummary.PlannedWorkdays}";
    public string LeaveHoursText => $"{_monthSummary.LeaveHours:0.#}";
    public string PtoDaysText => $"{_monthSummary.PtoDays}";

    public string ScheduleName
    {
        get
        {
            var s = _today.Computation.Schedule;
            if (!string.IsNullOrWhiteSpace(s.Name)) return s.Name;
            var current = _config.ScheduleProfiles.FirstOrDefault(p => p.Id == s.Id);
            return current?.Name ?? LocalizationService.Get("Salary.DefaultScheduleName");
        }
    }

    public bool IsFlexMode => _settings.DisplayMode == DisplayMode.Flex;
    public bool IsMiniMode => _settings.DisplayMode == DisplayMode.Mini;
    public bool IsNoneMode => _settings.DisplayMode == DisplayMode.None;
    public bool IsNormalMode => _settings.DisplayMode == DisplayMode.Normal;
    public double Opacity => _settings.Opacity;

    public ICommand OpenAboutCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenDetailCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand SetFlexModeCommand { get; }
    public ICommand SetMiniModeCommand { get; }
    public ICommand SetNoneModeCommand { get; }
    public ICommand SetNormalModeCommand { get; }

    public void Dispose()
    {
        _store.ConfigurationChanged -= OnConfigurationChanged;
        _wakeTimer?.Stop();
        _timer.Stop();
    }

    public void ResumeNotifications() => _notificationsSuspended = false;
    public void SuspendNotifications() => _notificationsSuspended = true;

    private void OnConfigurationChanged()
    {
        _settings = _store.CurrentSettings;
        _config = _store.CurrentConfiguration;
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
        if (!_timer.IsEnabled) _timer.Start();
        _nextMilestoneThreshold = NextMilestoneThresholdAbove(Earned, _settings.MilestoneAmount);
        _endOfDayReminderSent = false;
        RebuildMonthCache(DateTime.Now);
        HotkeySettingsChanged?.Invoke();
        Refresh();
    }

    public void ReloadSettings() => _store.Reload();

    public void NotifyUpdateAvailable(string version) =>
        NotificationRequested?.Invoke(
            LocalizationService.Get("Notification.UpdateAvailableTitle"),
            string.Format(LocalizationService.Get("Notification.UpdateAvailableBody"), version));

    private void OpenAbout()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is AboutWindow existing) { existing.Activate(); return; }
        }
        var win = new AboutWindow(_store);
        ApplyTopmostIfNeeded(win);
        win.Show();
    }

    private void OpenSettings()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is SettingsWindow existing) { existing.Activate(); return; }
        }
        var win = new SettingsWindow { DataContext = new SettingsViewModel(_store, this) };
        ApplyTopmostIfNeeded(win);
        win.Show();
    }

    private void OpenDetail()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is DetailWindow existing) { existing.Activate(); return; }
        }
        var win = new DetailWindow(_store.PayData, this);
        ApplyTopmostIfNeeded(win);
        win.Show();
    }

    // Function windows (Settings/About/Detail) must never be globally topmost: only the main
    // floating widget honors AlwaysOnTop. Win32 places owned windows of a TOPMOST owner in the
    // topmost band, so ownership is only applied while the widget itself is not topmost —
    // otherwise the function window would inherit the topmost band. When owned, the group
    // still drops behind other applications normally; when not owned (widget topmost), the
    // function window is fully independent in the normal Z-order.
    internal static void ApplyTopmostIfNeeded(Window win)
    {
        if (Application.Current.MainWindow is { Topmost: false } mainWindow && !ReferenceEquals(win, mainWindow))
        {
            win.Owner = mainWindow;
        }
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        if (today != _notifiedDate)
        {
            OnDayRollover(_notifiedDate, today);
            _notifiedDate = today;
            _nextMilestoneThreshold = _settings.MilestoneAmount;
            _endOfDayReminderSent = false;
        }

        if (today.Month != _cachedMonth.Month || today.Year != _cachedMonth.Year)
            RebuildMonthCache(now);

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
        OnPropertyChanged(nameof(IsRestDay));
        OnPropertyChanged(nameof(RestDayMessage));
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
                StatusKey = _today.Computation.Status == DayStatus.PublicHoliday ? "Status.Holiday" : "Status.Rest";
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
                StatusDetail = string.Format(LocalizationService.Get("Status.ResumeAt"), schedule.LunchSpan()?.End.ToString("HH:mm") ?? string.Empty);
                break;
            case DayPhase.AfterWork:
                StatusKey = _today.Computation.Status == DayStatus.Leave ? "Status.Leave" : "Status.Done";
                StatusDetail = string.Empty;
                break;
            case DayPhase.Working:
                StatusKey = _today.Computation.Status == DayStatus.Leave ? "Status.Leave" : "Status.Working";
                StatusDetail = string.Empty;
                break;
            default:
                StatusKey = "Status.Working";
                StatusDetail = string.Empty;
                break;
        }
    }

    private void OnDayRollover(DateOnly completedDay, DateOnly newDay)
    {
        try
        {
            var day = SalaryEngine.ComputeDay(_config, completedDay);
            _store.PayData.SnapshotDay(day, _config, _config.PlannedWorkdays(completedDay));
            var lastOfCompletedMonth = new DateOnly(completedDay.Year, completedDay.Month, DateTime.DaysInMonth(completedDay.Year, completedDay.Month));
            if (completedDay == lastOfCompletedMonth) FinalizeMonth(completedDay);
        }
        catch (Exception ex) { AppLogger.LogError("MainViewModel.OnDayRollover", ex); }
        RebuildMonthCache(DateTime.Now);
    }

    private void FinalizeMonth(DateOnly lastDay)
    {
        var summary = SalaryEngine.ComputeMonth(_config, lastDay, lastDay.AddDays(1), new TimeOnly(0, 0));
        var history = _store.PayData.History.Load(lastDay) ?? new MonthHistory { Month = $"{lastDay.Year:D4}-{lastDay.Month:D2}" };
        _store.PayData.History.FinalizeMonth(lastDay, history with
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

    private void RebuildMonthCache(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        _cachedMonth = new DateOnly(today.Year, today.Month, 1);
        _monthSummary = SalaryEngine.ComputeMonth(_config, _cachedMonth, today, TimeOnly.FromDateTime(now));
        _pastDaysEarned = 0m;
        foreach (var date in PayConfiguration.EachDay(_cachedMonth))
        {
            if (date >= today) break;
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

    private static decimal NextMilestoneThresholdAbove(decimal earned, decimal milestoneAmount)
    {
        if (milestoneAmount <= 0) return milestoneAmount;
        return (Math.Floor(earned / milestoneAmount) + 1) * milestoneAmount;
    }

    private void CheckNotifications(DateTime now)
    {
        if (_notificationsSuspended || !_today.Computation.IsPaidDay) return;
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
        if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
        _wakeTimer = new DispatcherTimer { Interval = delay };
        _wakeTimer.Tick += (_, _) => { _wakeTimer!.Stop(); _wakeTimer = null; _timer.Start(); Refresh(); };
        _wakeTimer.Start();
    }

    private void SetDisplayMode(DisplayMode mode)
    {
        if (_settings.DisplayMode == mode) return;
        _store.Commit(_settings with { DisplayMode = mode });
    }

    private static string FormatDuration(double seconds)
    {
        var total = (long)Math.Max(seconds, 0);
        return $"{total / 3600}h {(total % 3600) / 60:D2}m";
    }

    private static double SecondsOf(TimeOnly t) => t.Hour * 3600d + t.Minute * 60d;
}
