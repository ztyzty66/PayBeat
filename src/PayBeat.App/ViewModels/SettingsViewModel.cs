using PayBeat.App.Domain;
using PayBeat.App.Helpers;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.Views;
using System.ComponentModel;

namespace PayBeat.App.ViewModels;

/// <summary>Represents a language choice shown in the settings language dropdown.</summary>
/// <param name="Code">Language code stored in settings (e.g. <c>"en"</c>, <c>"zh-CN"</c>, <c>"auto"</c>).</param>
/// <param name="Name">Display name shown in the UI.</param>
public record LanguageOption(string Code, string Name);

/// <summary>Represents a theme choice shown in the settings theme dropdown.</summary>
/// <param name="Code">Theme code stored in settings (e.g. <c>"auto"</c>, <c>"light"</c>, <c>"dark"</c>).</param>
/// <param name="Name">Display name shown in the UI.</param>
public record ThemeOption(string Code, string Name);

/// <summary>
/// View model for the settings window. Versioning rule enforced here: editing salary, schedule
/// times, or the week policy creates/updates a version whose EffectiveFrom is *today* — earlier
/// months keep their own versions, so history is never rewritten by a settings save.
/// </summary>
public class SettingsViewModel : ViewModelBase, IDataErrorInfo
{
    private readonly MainViewModel _mainVm;
    private readonly SettingsService _service;
    private PayConfiguration _config;

    private bool _alwaysOnTop;
    private string _amountText;
    private SalaryMode _salaryMode;
    private DisplayMode _displayMode;
    private bool _enableEndOfDayReminder;
    private bool _enableMilestoneNotifications;
    private string _endOfDayReminderMinutesText;
    private int _hotkeyModifiers;
    private int _hotkeyVirtualKey;
    private string _language;
    private bool _lunchBreakEnabled;
    private TimeOnly _lunchBreakEnd;
    private TimeOnly _lunchBreakStart;
    private string _milestoneAmountText;
    private double _opacity;
    private int _refreshInterval;
    private bool _runAtStartup;
    private string _theme;
    private TimeOnly _workEnd;
    private TimeOnly _workStart;
    private WorkWeekType _weekType;
    private string _scheduleName;
    private readonly HashSet<DayOfWeek> _workDays = [];

    /// <summary>
    /// Initializes a new instance of <see cref="SettingsViewModel"/>, populating fields from the
    /// configuration currently effective today.
    /// </summary>
    /// <param name="service">Service used to load and persist settings.</param>
    /// <param name="mainVm">Main view model; <see cref="MainViewModel.ReloadSettings"/> is called after saving.</param>
    public SettingsViewModel(SettingsService service, MainViewModel mainVm)
    {
        _service = service;
        _mainVm = mainVm;

        var settings = service.Load();
        _config = new PayDataService(service, new HistoryService()).BuildConfiguration(settings);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var profile = _config.ResolveSalaryProfile(today);
        var schedule = _config.ResolveSchedule(today);
        var policy = _config.ResolveWeekPolicy(today);

        _salaryMode = profile.Mode;
        _amountText = (profile.Mode == SalaryMode.Monthly ? profile.MonthlyAmount : profile.DailyAmount).ToString("G29");
        _workStart = schedule.WorkStart;
        _workEnd = schedule.WorkEnd;
        _lunchBreakEnabled = schedule.LunchBreakEnabled;
        _lunchBreakStart = schedule.LunchBreakStart;
        _lunchBreakEnd = schedule.LunchBreakEnd;
        _scheduleName = string.IsNullOrWhiteSpace(schedule.Name)
            ? LocalizationService.Get("Salary.DefaultScheduleName")
            : schedule.Name;
        _weekType = policy.Type;
        foreach (var day in policy.WorkDays)
        {
            _workDays.Add(day);
        }

        _displayMode = settings.DisplayMode;
        _alwaysOnTop = settings.AlwaysOnTop;
        _opacity = settings.Opacity;
        _refreshInterval = settings.RefreshInterval;
        _language = settings.Language;
        _theme = settings.Theme;
        _hotkeyModifiers = settings.HotkeyModifiers;
        _hotkeyVirtualKey = settings.HotkeyVirtualKey;
        _runAtStartup = StartupService.IsEnabled();
        _enableEndOfDayReminder = settings.EnableEndOfDayReminder;
        _endOfDayReminderMinutesText = settings.EndOfDayReminderMinutes.ToString();
        _enableMilestoneNotifications = settings.EnableMilestoneNotifications;
        _milestoneAmountText = settings.MilestoneAmount.ToString("G29");

        SaveCommand = new RelayCommand(Save, CanSave);
        CancelCommand = new RelayCommand(CloseWindow);
        ManageSchedulesCommand = new RelayCommand(OpenScheduleManager);
    }

    /// <summary>Binds to the Always on Top checkbox in the settings window.</summary>
    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => SetField(ref _alwaysOnTop, value);
    }

    /// <summary>Fixed list of language options shown in the language dropdown.</summary>
    public IReadOnlyList<LanguageOption> AvailableLanguages
    {
        get;
    } =
        [
        new("auto", "Auto"),
        new("en", "English"),
        new("zh-CN", "中文"),
    ];

    /// <summary>Fixed list of theme options shown in the theme dropdown.</summary>
    public IReadOnlyList<ThemeOption> AvailableThemes
    {
        get;
    } =
        [
        new("auto", "Auto"),
        new("light", "Light"),
        new("dark", "Dark"),
    ];

    /// <summary>Closes the settings window without saving.</summary>
    public ICommand CancelCommand
    {
        get;
    }

    /// <summary>
    /// Raw text entered in the amount field. Changing this re-evaluates <see cref="SaveCommand"/> availability.
    /// </summary>
    public string AmountText
    {
        get => _amountText;
        set
        {
            SetField(ref _amountText, value);
            Revalidate();
        }
    }

    /// <summary>Selected salary mode (monthly/daily).</summary>
    public SalaryMode SalaryMode
    {
        get => _salaryMode;
        set => SetField(ref _salaryMode, value);
    }

    /// <summary>Proxy for the "monthly" segmented button.</summary>
    public bool IsMonthlyMode
    {
        get => _salaryMode == SalaryMode.Monthly;
        set
        {
            if (value)
            {
                SalaryMode = SalaryMode.Monthly;
            }
        }
    }

    /// <summary>Proxy for the "daily" segmented button.</summary>
    public bool IsDailyMode
    {
        get => _salaryMode == SalaryMode.Daily;
        set
        {
            if (value)
            {
                SalaryMode = SalaryMode.Daily;
            }
        }
    }

    /// <summary>Selected week policy preset.</summary>
    public WorkWeekType WeekType
    {
        get => _weekType;
        set
        {
            if (SetField(ref _weekType, value))
            {
                ApplyWeekPreset(value);
            }
        }
    }

    /// <summary>Proxy for the double-rest segmented button.</summary>
    public bool IsDoubleRest
    {
        get => _weekType == WorkWeekType.DoubleRest;
        set
        {
            if (value)
            {
                WeekType = WorkWeekType.DoubleRest;
            }
        }
    }

    /// <summary>Proxy for the single-rest segmented button.</summary>
    public bool IsSingleRest
    {
        get => _weekType == WorkWeekType.SingleRest;
        set
        {
            if (value)
            {
                WeekType = WorkWeekType.SingleRest;
            }
        }
    }

    /// <summary>Proxy for the custom segmented button.</summary>
    public bool IsCustomWeek
    {
        get => _weekType == WorkWeekType.Custom;
        set
        {
            if (value)
            {
                WeekType = WorkWeekType.Custom;
            }
        }
    }

    /// <summary>Whether Monday is a working day (editable toggle).</summary>
    public bool WorkMonday
    {
        get => _workDays.Contains(DayOfWeek.Monday);
        set => SetWorkDay(DayOfWeek.Monday, value);
    }

    /// <summary>Whether Tuesday is a working day (editable toggle).</summary>
    public bool WorkTuesday
    {
        get => _workDays.Contains(DayOfWeek.Tuesday);
        set => SetWorkDay(DayOfWeek.Tuesday, value);
    }

    /// <summary>Whether Wednesday is a working day (editable toggle).</summary>
    public bool WorkWednesday
    {
        get => _workDays.Contains(DayOfWeek.Wednesday);
        set => SetWorkDay(DayOfWeek.Wednesday, value);
    }

    /// <summary>Whether Thursday is a working day (editable toggle).</summary>
    public bool WorkThursday
    {
        get => _workDays.Contains(DayOfWeek.Thursday);
        set => SetWorkDay(DayOfWeek.Thursday, value);
    }

    /// <summary>Whether Friday is a working day (editable toggle).</summary>
    public bool WorkFriday
    {
        get => _workDays.Contains(DayOfWeek.Friday);
        set => SetWorkDay(DayOfWeek.Friday, value);
    }

    /// <summary>Whether Saturday is a working day (editable toggle).</summary>
    public bool WorkSaturday
    {
        get => _workDays.Contains(DayOfWeek.Saturday);
        set => SetWorkDay(DayOfWeek.Saturday, value);
    }

    /// <summary>Whether Sunday is a working day (editable toggle).</summary>
    public bool WorkSunday
    {
        get => _workDays.Contains(DayOfWeek.Sunday);
        set => SetWorkDay(DayOfWeek.Sunday, value);
    }

    /// <summary>Name of the schedule being edited.</summary>
    public string ScheduleName
    {
        get => _scheduleName;
        set => SetField(ref _scheduleName, value);
    }

    /// <summary>Opens the schedule manager window.</summary>
    public ICommand ManageSchedulesCommand
    {
        get;
    }

    /// <summary>Exposes the settings store for child views (calendar page).</summary>
    public SettingsService Service => _service;

    /// <summary>Exposes the main view model for child views (calendar page reloads).</summary>
    public MainViewModel Main => _mainVm;

    /// <summary>
    /// Selected display mode in the settings window. Setting this also raises change notifications
    /// for <see cref="IsNormalMode"/> and <see cref="IsMiniMode"/>.
    /// </summary>
    public DisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (SetField(ref _displayMode, value))
            {
                OnPropertyChanged(nameof(IsNormalMode));
                OnPropertyChanged(nameof(IsMiniMode));
                OnPropertyChanged(nameof(IsNoneMode));
                OnPropertyChanged(nameof(IsFlexMode));
            }
        }
    }

    /// <summary>Whether the end-of-day reminder tray notification is enabled.</summary>
    public bool EnableEndOfDayReminder
    {
        get => _enableEndOfDayReminder;
        set
        {
            SetField(ref _enableEndOfDayReminder, value);
            Revalidate();
        }
    }

    /// <summary>Whether the milestone earnings tray notification is enabled.</summary>
    public bool EnableMilestoneNotifications
    {
        get => _enableMilestoneNotifications;
        set
        {
            SetField(ref _enableMilestoneNotifications, value);
            Revalidate();
        }
    }

    /// <summary>Raw text entered in the end-of-day reminder minutes field; must be an integer in [1, 60].</summary>
    public string EndOfDayReminderMinutesText
    {
        get => _endOfDayReminderMinutesText;
        set
        {
            SetField(ref _endOfDayReminderMinutesText, value);
            Revalidate();
        }
    }

    /// <summary>Unused; per-field errors are reported via the indexer instead.</summary>
    string IDataErrorInfo.Error => string.Empty;

    /// <summary>Validation error message shown below the Save button; empty string when there is no error.</summary>
    public string ErrorMessage
    {
        get;
        private set => SetField(ref field, value);
    } = string.Empty;

    /// <summary>Human-readable hotkey string (e.g. <c>Ctrl+Alt+X</c>) shown in the hotkey field.</summary>
    public string HotkeyDisplayText => HotkeyService.Format(HotkeyModifiers, HotkeyVirtualKey);

    /// <summary>
    /// Win32 modifier flags for the hotkey. Setting this raises <see cref="HotkeyDisplayText"/> change.
    /// </summary>
    public int HotkeyModifiers
    {
        get => _hotkeyModifiers;
        set
        {
            if (SetField(ref _hotkeyModifiers, value))
            {
                OnPropertyChanged(nameof(HotkeyDisplayText));
            }
        }
    }

    /// <summary>
    /// Virtual-key code for the hotkey. Setting this raises <see cref="HotkeyDisplayText"/> change.
    /// </summary>
    public int HotkeyVirtualKey
    {
        get => _hotkeyVirtualKey;
        set
        {
            if (SetField(ref _hotkeyVirtualKey, value))
            {
                OnPropertyChanged(nameof(HotkeyDisplayText));
            }
        }
    }

    /// <summary>Proxy property for the Flex radio button; sets <see cref="DisplayMode"/> when assigned <see langword="true"/>.</summary>
    public bool IsFlexMode
    {
        get => _displayMode == DisplayMode.Flex;
        set
        {
            if (value)
            {
                DisplayMode = DisplayMode.Flex;
            }
        }
    }

    /// <summary>Proxy property for the Mini radio button; sets <see cref="DisplayMode"/> when assigned <see langword="true"/>.</summary>
    public bool IsMiniMode
    {
        get => _displayMode == DisplayMode.Mini;
        set
        {
            if (value)
            {
                DisplayMode = DisplayMode.Mini;
            }
        }
    }

    /// <summary>Proxy property for the None radio button; sets <see cref="DisplayMode"/> when assigned <see langword="true"/>.</summary>
    public bool IsNoneMode
    {
        get => _displayMode == DisplayMode.None;
        set
        {
            if (value)
            {
                DisplayMode = DisplayMode.None;
            }
        }
    }

    /// <summary>Proxy property for the Normal radio button; sets <see cref="DisplayMode"/> when assigned <see langword="true"/>.</summary>
    public bool IsNormalMode
    {
        get => _displayMode == DisplayMode.Normal;
        set
        {
            if (value)
            {
                DisplayMode = DisplayMode.Normal;
            }
        }
    }

    /// <summary>Selected language code (e.g. <c>"auto"</c>, <c>"en"</c>, <c>"zh-CN"</c>).</summary>
    public string Language
    {
        get => _language;
        set => SetField(ref _language, value);
    }

    /// <summary>Whether a lunch break is deducted from earnings.</summary>
    public bool LunchBreakEnabled
    {
        get => _lunchBreakEnabled;
        set => SetField(ref _lunchBreakEnabled, value);
    }

    /// <summary>Lunch break end time.</summary>
    public TimeOnly LunchBreakEnd
    {
        get => _lunchBreakEnd;
        set => SetField(ref _lunchBreakEnd, value);
    }

    /// <summary>Lunch break start time.</summary>
    public TimeOnly LunchBreakStart
    {
        get => _lunchBreakStart;
        set => SetField(ref _lunchBreakStart, value);
    }

    /// <summary>Raw text entered in the milestone amount field.</summary>
    public string MilestoneAmountText
    {
        get => _milestoneAmountText;
        set
        {
            SetField(ref _milestoneAmountText, value);
            Revalidate();
        }
    }

    /// <summary>Widget opacity at idle, clamped to [0.1, 1.0].</summary>
    public double Opacity
    {
        get => _opacity;
        set => SetField(ref _opacity, Math.Clamp(value, 0.1, 1.0));
    }

    /// <summary>Earnings refresh interval in seconds, clamped to [1, 60].</summary>
    public int RefreshInterval
    {
        get => _refreshInterval;
        set => SetField(ref _refreshInterval, Math.Clamp(value, 1, 60));
    }

    /// <summary>Whether the app is registered to launch at Windows startup.</summary>
    public bool RunAtStartup
    {
        get => _runAtStartup;
        set => SetField(ref _runAtStartup, value);
    }

    /// <summary>Validates input and persists all settings. Disabled when validation fails.</summary>
    public ICommand SaveCommand
    {
        get;
    }

    /// <summary>Selected theme code (e.g. <c>"auto"</c>, <c>"light"</c>, <c>"dark"</c>).</summary>
    public string Theme
    {
        get => _theme;
        set => SetField(ref _theme, value);
    }

    /// <summary>Work day end time.</summary>
    public TimeOnly WorkEnd
    {
        get => _workEnd;
        set => SetField(ref _workEnd, value);
    }

    /// <summary>Work day start time.</summary>
    public TimeOnly WorkStart
    {
        get => _workStart;
        set => SetField(ref _workStart, value);
    }

    /// <summary>Per-field validation error shown as a bubble popup next to the offending input.</summary>
    string IDataErrorInfo.this[string columnName] => columnName switch
    {
        nameof(AmountText) => ValidateAmount() ?? string.Empty,
        nameof(EndOfDayReminderMinutesText) => EnableEndOfDayReminder ? ValidateEndOfDayReminderMinutes() ?? string.Empty : string.Empty,
        nameof(MilestoneAmountText) => EnableMilestoneNotifications ? ValidateMilestoneAmount() ?? string.Empty : string.Empty,
        _ => string.Empty,
    };

    private bool CanSave() => Validate() is null;

    private void CloseWindow()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is SettingsWindow)
            {
                w.Close();
                break;
            }
        }
    }

    private void OpenScheduleManager()
    {
        var win = new ScheduleManagerWindow(_service, _mainVm) { Owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault() };
        win.ShowDialog();
        ReloadFromService();
    }

    /// <summary>Re-reads persisted settings after the schedule manager may have changed them.</summary>
    private void ReloadFromService()
    {
        var settings = _service.Load();
        _config = new PayDataService(_service, new HistoryService()).BuildConfiguration(settings);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var schedule = _config.ResolveSchedule(today);
        _workStart = schedule.WorkStart;
        _workEnd = schedule.WorkEnd;
        _lunchBreakEnabled = schedule.LunchBreakEnabled;
        _lunchBreakStart = schedule.LunchBreakStart;
        _lunchBreakEnd = schedule.LunchBreakEnd;
        OnPropertyChanged(nameof(WorkStart));
        OnPropertyChanged(nameof(WorkEnd));
        OnPropertyChanged(nameof(LunchBreakEnabled));
        OnPropertyChanged(nameof(LunchBreakStart));
        OnPropertyChanged(nameof(LunchBreakEnd));
        Revalidate();
    }

    private void ApplyWeekPreset(WorkWeekType type)
    {
        _workDays.Clear();
        foreach (var day in WorkWeekPolicy.Create(type, new DateOnly(2000, 1, 1)).WorkDays)
        {
            _workDays.Add(day);
        }

        OnPropertyChanged(nameof(WorkMonday));
        OnPropertyChanged(nameof(WorkTuesday));
        OnPropertyChanged(nameof(WorkWednesday));
        OnPropertyChanged(nameof(WorkThursday));
        OnPropertyChanged(nameof(WorkFriday));
        OnPropertyChanged(nameof(WorkSaturday));
        OnPropertyChanged(nameof(WorkSunday));
    }

    private void SetWorkDay(DayOfWeek day, bool isWorkDay)
    {
        if (isWorkDay)
        {
            _workDays.Add(day);
        }
        else
        {
            _workDays.Remove(day);
        }

        OnPropertyChanged(day switch
        {
            DayOfWeek.Monday => nameof(WorkMonday),
            DayOfWeek.Tuesday => nameof(WorkTuesday),
            DayOfWeek.Wednesday => nameof(WorkWednesday),
            DayOfWeek.Thursday => nameof(WorkThursday),
            DayOfWeek.Friday => nameof(WorkFriday),
            DayOfWeek.Saturday => nameof(WorkSaturday),
            _ => nameof(WorkSunday),
        });
    }

    /// <summary>
    /// Updates the bottom-of-window error message and Save availability. Only schedule-related errors are
    /// shown here — the amount, milestone amount, and reminder minutes fields report their own errors
    /// via a bubble popup next to the field instead.
    /// </summary>
    private void Revalidate()
    {
        ErrorMessage = ValidateSchedule() ?? string.Empty;
        ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Persists everything. Versioning: salary/schedule/week changes take effect from *today* —
    /// if the latest version already starts today it is updated in place, otherwise a new version
    /// is appended. Older versions are never touched.
    /// </summary>
    private void Save()
    {
        if (Validate() is not null)
        {
            ErrorMessage = ValidateSchedule() ?? string.Empty;
            return;
        }

        var existing = _service.Load();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var amount = Math.Round(decimal.Parse(_amountText), 2);

        // ── salary profile (versioned) ──
        var profiles = new List<SalaryProfile>(existing.SalaryProfiles);
        var latestProfile = profiles.Count > 0
            ? profiles.OrderByDescending(p => p.EffectiveFrom).First()
            : null;
        var desiredProfile = new SalaryProfile
        {
            Mode = _salaryMode,
            MonthlyAmount = _salaryMode == SalaryMode.Monthly ? amount : 0m,
            DailyAmount = _salaryMode == SalaryMode.Daily ? amount : 0m,
            EffectiveFrom = today,
        };
        if (latestProfile is null)
        {
            profiles.Add(desiredProfile with { EffectiveFrom = new DateOnly(2000, 1, 1) });
        }
        else if (latestProfile.EffectiveFrom == today)
        {
            profiles[profiles.IndexOf(latestProfile)] = desiredProfile;
        }
        else if (latestProfile.Mode != _salaryMode
                 || (_salaryMode == SalaryMode.Monthly ? latestProfile.MonthlyAmount : latestProfile.DailyAmount) != amount)
        {
            profiles.Add(desiredProfile);
        }

        // ── schedule (versioned) ──
        var schedules = new List<WorkScheduleProfile>(existing.ScheduleProfiles);
        var latestSchedule = schedules.Count > 0
            ? schedules.OrderByDescending(s => s.EffectiveFrom).First()
            : null;
        var desiredSchedule = new WorkScheduleProfile
        {
            Id = latestSchedule?.EffectiveFrom == today ? latestSchedule.Id : Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(_scheduleName) ? LocalizationService.Get("Salary.DefaultScheduleName") : _scheduleName.Trim(),
            WorkStart = _workStart,
            WorkEnd = _workEnd,
            LunchBreakEnabled = _lunchBreakEnabled,
            LunchBreakStart = _lunchBreakStart,
            LunchBreakEnd = _lunchBreakEnd,
            EffectiveFrom = today,
        };
        if (latestSchedule is null)
        {
            schedules.Add(desiredSchedule with { EffectiveFrom = new DateOnly(2000, 1, 1) });
        }
        else if (latestSchedule.EffectiveFrom == today)
        {
            schedules[schedules.IndexOf(latestSchedule)] = desiredSchedule;
        }
        else if (!ScheduleEquals(latestSchedule, desiredSchedule))
        {
            schedules.Add(desiredSchedule);
        }

        // ── week policy (versioned) ──
        var policies = new List<WorkWeekPolicy>(existing.WeekPolicies);
        var latestPolicy = policies.Count > 0
            ? policies.OrderByDescending(p => p.EffectiveFrom).First()
            : null;
        var desiredPolicy = new WorkWeekPolicy
        {
            Type = _weekType,
            WorkDays = new HashSet<DayOfWeek>(_workDays),
            EffectiveFrom = today,
        };
        if (latestPolicy is null)
        {
            policies.Add(desiredPolicy with { EffectiveFrom = new DateOnly(2000, 1, 1) });
        }
        else if (latestPolicy.EffectiveFrom == today)
        {
            policies[policies.IndexOf(latestPolicy)] = desiredPolicy;
        }
        else if (latestPolicy.Type != _weekType || !latestPolicy.WorkDays.SetEquals(_workDays))
        {
            policies.Add(desiredPolicy);
        }

        var settings = existing with
        {
            DisplayMode = DisplayMode,
            AlwaysOnTop = AlwaysOnTop,
            Opacity = Opacity,
            RefreshInterval = RefreshInterval,
            Language = Language,
            Theme = Theme,
            HotkeyModifiers = HotkeyModifiers,
            HotkeyVirtualKey = HotkeyVirtualKey,
            EnableEndOfDayReminder = EnableEndOfDayReminder,
            EndOfDayReminderMinutes = EnableEndOfDayReminder && int.TryParse(_endOfDayReminderMinutesText, out var parsedMinutes)
                ? parsedMinutes
                : existing.EndOfDayReminderMinutes,
            EnableMilestoneNotifications = EnableMilestoneNotifications,
            MilestoneAmount = EnableMilestoneNotifications && decimal.TryParse(_milestoneAmountText, out var parsedMilestone)
                ? Math.Round(parsedMilestone, 2)
                : existing.MilestoneAmount,
            SalaryProfiles = profiles,
            ScheduleProfiles = schedules,
            WeekPolicies = policies,
            SetupCompleted = true,
        };

        _service.Save(settings);
        StartupService.SetEnabled(_runAtStartup);
        LocalizationService.Apply(Language);
        ThemeService.Apply(Theme);
        _mainVm.ReloadSettings();

        CloseWindow();
    }

    private static bool ScheduleEquals(WorkScheduleProfile a, WorkScheduleProfile b) =>
        a.WorkStart == b.WorkStart
        && a.WorkEnd == b.WorkEnd
        && a.LunchBreakEnabled == b.LunchBreakEnabled
        && a.LunchBreakStart == b.LunchBreakStart
        && a.LunchBreakEnd == b.LunchBreakEnd;

    /// <summary>Validates all fields and returns the first error message, or <see langword="null"/> when everything is valid.</summary>
    private string? Validate()
    {
        var amountError = ValidateAmount();
        if (amountError is not null)
        {
            return amountError;
        }
        var scheduleError = ValidateSchedule();
        if (scheduleError is not null)
        {
            return scheduleError;
        }
        if (EnableEndOfDayReminder)
        {
            var minutesError = ValidateEndOfDayReminderMinutes();
            if (minutesError is not null)
            {
                return minutesError;
            }
        }
        if (EnableMilestoneNotifications)
        {
            var milestoneError = ValidateMilestoneAmount();
            if (milestoneError is not null)
            {
                return milestoneError;
            }
        }
        if (_workDays.Count == 0)
        {
            return LocalizationService.Get("Error.WorkDayRequired");
        }

        return null;
    }

    /// <summary>Validates <see cref="AmountText"/>; returns <see langword="null"/> when valid.</summary>
    private string? ValidateAmount()
    {
        if (!decimal.TryParse(_amountText, out var amount) || amount <= 0)
        {
            return LocalizationService.Get("Error.SalaryPositive");
        }
        if (amount > SalarySettings.MaxDailySalary)
        {
            return LocalizationService.Get("Error.SalaryTooLarge");
        }

        return null;
    }

    /// <summary>Validates <see cref="EndOfDayReminderMinutesText"/>; returns <see langword="null"/> when valid.</summary>
    private string? ValidateEndOfDayReminderMinutes()
    {
        if (!int.TryParse(_endOfDayReminderMinutesText, out var minutes) || minutes < 1 || minutes > 60)
        {
            return LocalizationService.Get("Error.EndOfDayReminderMinutesInvalid");
        }

        return null;
    }

    /// <summary>Validates <see cref="MilestoneAmountText"/>; returns <see langword="null"/> when valid.</summary>
    private string? ValidateMilestoneAmount()
    {
        if (!decimal.TryParse(_milestoneAmountText, out var milestone) || milestone <= 0)
        {
            return LocalizationService.Get("Error.MilestoneAmountPositive");
        }
        if (decimal.TryParse(_amountText, out var daily) && milestone > daily)
        {
            return LocalizationService.Get("Error.MilestoneAmountTooLarge");
        }

        return null;
    }

    /// <summary>Validates work hours and lunch break; returns <see langword="null"/> when valid.</summary>
    private string? ValidateSchedule()
    {
        if (WorkStart >= WorkEnd)
        {
            return LocalizationService.Get("Error.WorkEndAfterStart");
        }
        if (LunchBreakEnabled && (LunchBreakStart >= LunchBreakEnd || LunchBreakStart < WorkStart || LunchBreakEnd > WorkEnd))
        {
            return LocalizationService.Get("Error.LunchBreakInvalid");
        }

        return null;
    }
}
