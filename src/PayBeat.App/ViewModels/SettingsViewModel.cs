using PayBeat.App.Domain;
using PayBeat.App.Helpers;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.Views;
using System.ComponentModel;

namespace PayBeat.App.ViewModels;

/// <summary>Represents a language choice shown in the settings language dropdown.</summary>
public record LanguageOption(string Code, string Name);

/// <summary>Represents a theme choice shown in the settings theme dropdown.</summary>
public record ThemeOption(string Code, string Name);

/// <summary>
/// View model for the settings window. Uses a shared <see cref="ConfigurationDraft"/> so that
/// all child editors (salary, schedule, calendar) operate on the same mutable snapshot.
/// </summary>
public class SettingsViewModel : ViewModelBase, IDataErrorInfo
{
    private readonly MainViewModel _mainVm;
    private readonly ConfigurationStore _store;
    private readonly ConfigurationDraft _draft;
    private readonly UpdateService _updateService;
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
    private string _hotkeyStatus = "";
    private bool _originalRunAtStartup;

    // ── Update state ───────────────────────────────────────────────────────
    private string _updateStatusText = "";
    private string _updateReleaseNotes = "";
    private int _downloadProgress;
    private bool _isChecking;
    private bool _isDownloading;
    private bool _hasUpdateAvailable;
    private string? _pendingDownloadUrl;
    private string? _pendingSha256;

    /// <summary>Which effective date the salary amount and work policy apply from.</summary>
    public enum EffectiveDateChoice
    {
        /// <summary>First day of the current month (default: the whole current month follows the new rule).</summary>
        FirstOfMonth = 0,

        /// <summary>Today (mid-month switches, user's explicit choice).</summary>
        Today = 1,

        /// <summary>A user-typed date (explicit versioned behaviour).</summary>
        Custom = 2,
    }

    private EffectiveDateChoice _effectiveChoice = EffectiveDateChoice.FirstOfMonth;
    private string _customEffectiveDateText = "";

    public SettingsViewModel(ConfigurationStore store, MainViewModel mainVm, UpdateService? updateService = null)
    {
        _store = store;
        _mainVm = mainVm;
        _updateService = updateService ?? new UpdateService();
        _draft = store.CreateDraft();
        _config = _draft.BuildPreviewConfiguration(store.PayData);

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
        _scheduleName = string.IsNullOrWhiteSpace(schedule.Name) ? LocalizationService.Get("Salary.DefaultScheduleName") : schedule.Name;
        _weekType = policy.Type;
        foreach (var day in policy.WorkDays) _workDays.Add(day);

        _displayMode = _draft.DisplayMode;
        _alwaysOnTop = _draft.AlwaysOnTop;
        _opacity = _draft.Opacity;
        _refreshInterval = _draft.RefreshInterval;
        _language = _draft.Language;
        _theme = _draft.Theme;
        _hotkeyModifiers = _draft.HotkeyModifiers;
        _hotkeyVirtualKey = _draft.HotkeyVirtualKey;
        _runAtStartup = StartupService.IsEnabled();
        _originalRunAtStartup = _runAtStartup;
        _enableEndOfDayReminder = _draft.EnableEndOfDayReminder;
        _endOfDayReminderMinutesText = _draft.EndOfDayReminderMinutes.ToString();
        _enableMilestoneNotifications = _draft.EnableMilestoneNotifications;
        _milestoneAmountText = _draft.MilestoneAmount.ToString("G29");
        _effectiveChoice = EffectiveDateChoice.FirstOfMonth;
        _customEffectiveDateText = new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1).ToString("yyyy-MM-dd");

        SaveCommand = new RelayCommand(Save, CanSave);
        CancelCommand = new RelayCommand(CloseWindow);
        ManageSchedulesCommand = new RelayCommand(OpenScheduleManager);
        CheckUpdateCommand = new RelayCommand(() => _ = CheckUpdateAsync(), () => CanCheckUpdate);
        DownloadInstallCommand = new RelayCommand(() => _ = DownloadInstallAsync(), () => CanDownloadInstall);
        RefreshHotkeyStatus();
    }

    public ConfigurationDraft Draft => _draft;
    public ConfigurationStore Store => _store;
    public MainViewModel Main => _mainVm;

    public bool AlwaysOnTop { get => _alwaysOnTop; set => SetField(ref _alwaysOnTop, value); }
    public IReadOnlyList<LanguageOption> AvailableLanguages { get; } = [new("auto", "Auto"), new("en", "English"), new("zh-CN", "中文")];
    public IReadOnlyList<ThemeOption> AvailableThemes { get; } = [new("auto", "Auto"), new("light", "Light"), new("dark", "Dark")];
    public ICommand CancelCommand { get; }
    public string AmountText { get => _amountText; set { SetField(ref _amountText, value); Revalidate(); } }
    public SalaryMode SalaryMode { get => _salaryMode; set => SetField(ref _salaryMode, value); }
    public bool IsMonthlyMode { get => _salaryMode == SalaryMode.Monthly; set { if (value) SalaryMode = SalaryMode.Monthly; } }
    public bool IsDailyMode { get => _salaryMode == SalaryMode.Daily; set { if (value) SalaryMode = SalaryMode.Daily; } }

    public WorkWeekType WeekType { get => _weekType; set { if (SetField(ref _weekType, value)) { ApplyWeekPreset(value); ApplyWeekToDraft(); } } }
    public bool IsDoubleRest { get => _weekType == WorkWeekType.DoubleRest; set { if (value) WeekType = WorkWeekType.DoubleRest; } }
    public bool IsSingleRest { get => _weekType == WorkWeekType.SingleRest; set { if (value) WeekType = WorkWeekType.SingleRest; } }
    public bool IsCustomWeek { get => _weekType == WorkWeekType.Custom; set { if (value) WeekType = WorkWeekType.Custom; } }

    public bool WorkMonday { get => _workDays.Contains(DayOfWeek.Monday); set => SetWorkDay(DayOfWeek.Monday, value); }
    public bool WorkTuesday { get => _workDays.Contains(DayOfWeek.Tuesday); set => SetWorkDay(DayOfWeek.Tuesday, value); }
    public bool WorkWednesday { get => _workDays.Contains(DayOfWeek.Wednesday); set => SetWorkDay(DayOfWeek.Wednesday, value); }
    public bool WorkThursday { get => _workDays.Contains(DayOfWeek.Thursday); set => SetWorkDay(DayOfWeek.Thursday, value); }
    public bool WorkFriday { get => _workDays.Contains(DayOfWeek.Friday); set => SetWorkDay(DayOfWeek.Friday, value); }
    public bool WorkSaturday { get => _workDays.Contains(DayOfWeek.Saturday); set => SetWorkDay(DayOfWeek.Saturday, value); }
    public bool WorkSunday { get => _workDays.Contains(DayOfWeek.Sunday); set => SetWorkDay(DayOfWeek.Sunday, value); }

    public string ScheduleName { get => _scheduleName; set => SetField(ref _scheduleName, value); }

    /// <summary>Which effective date salary/work-policy changes apply from (default: first of month).</summary>
    public EffectiveDateChoice Choice
    {
        get => _effectiveChoice;
        set
        {
            if (SetField(ref _effectiveChoice, value))
            {
                // The radio proxies derive from Choice — they must be notified so the
                // custom-date box enables/disables when the selection changes.
                OnPropertyChanged(nameof(IsEffectiveFirstOfMonth));
                OnPropertyChanged(nameof(IsEffectiveToday));
                OnPropertyChanged(nameof(IsEffectiveCustom));
                Revalidate();
            }
        }
    }

    /// <summary>Radio proxy: 本月1日起.</summary>
    public bool IsEffectiveFirstOfMonth
    {
        get => _effectiveChoice == EffectiveDateChoice.FirstOfMonth;
        set { if (value) Choice = EffectiveDateChoice.FirstOfMonth; }
    }

    /// <summary>Radio proxy: 今天起.</summary>
    public bool IsEffectiveToday
    {
        get => _effectiveChoice == EffectiveDateChoice.Today;
        set { if (value) Choice = EffectiveDateChoice.Today; }
    }

    /// <summary>Radio proxy: 自定义日期.</summary>
    public bool IsEffectiveCustom
    {
        get => _effectiveChoice == EffectiveDateChoice.Custom;
        set { if (value) Choice = EffectiveDateChoice.Custom; }
    }

    /// <summary>Custom effective date text (yyyy-MM-dd), used when <see cref="Choice"/> is Custom.</summary>
    public string CustomEffectiveDateText
    {
        get => _customEffectiveDateText;
        set { if (SetField(ref _customEffectiveDateText, value)) Revalidate(); }
    }

    /// <summary>Resolves the currently selected effective date for salary/work-policy edits.</summary>
    public DateOnly ResolveEffectiveDate()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        switch (_effectiveChoice)
        {
            case EffectiveDateChoice.Today:
                return today;
            case EffectiveDateChoice.Custom when DateOnly.TryParseExact(_customEffectiveDateText.Trim(), "yyyy-MM-dd", out var custom):
                return custom;
            default:
                return new DateOnly(today.Year, today.Month, 1);
        }
    }

    /// <summary>Name of the schedule currently effective today (read-only display in card C).</summary>
    public string CurrentScheduleName => _config.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now)) is { } s && !string.IsNullOrWhiteSpace(s.Name)
        ? s.Name
        : LocalizationService.Get("Salary.DefaultScheduleName");
    public ICommand ManageSchedulesCommand { get; }
    public DisplayMode DisplayMode { get => _displayMode; set { if (SetField(ref _displayMode, value)) { OnPropertyChanged(nameof(IsNormalMode)); OnPropertyChanged(nameof(IsMiniMode)); OnPropertyChanged(nameof(IsNoneMode)); OnPropertyChanged(nameof(IsFlexMode)); } } }
    public bool EnableEndOfDayReminder { get => _enableEndOfDayReminder; set { SetField(ref _enableEndOfDayReminder, value); Revalidate(); } }
    public bool EnableMilestoneNotifications { get => _enableMilestoneNotifications; set { SetField(ref _enableMilestoneNotifications, value); Revalidate(); } }
    public string EndOfDayReminderMinutesText { get => _endOfDayReminderMinutesText; set { SetField(ref _endOfDayReminderMinutesText, value); Revalidate(); } }
    string IDataErrorInfo.Error => string.Empty;
    public string ErrorMessage { get; private set => SetField(ref field, value); } = string.Empty;
    public string HotkeyDisplayText => HotkeyService.Format(HotkeyModifiers, HotkeyVirtualKey);

    /// <summary>
    /// Current hotkey registration state for display: empty (never registered),
    /// "✓ 快捷键可用" (registered), or a ⚠ warning naming the occupied combination.
    /// </summary>
    public string HotkeyStatus
    {
        get => _hotkeyStatus;
        private set => SetField(ref _hotkeyStatus, value);
    }

    /// <summary>Re-evaluates <see cref="HotkeyStatus"/> from the latest registration result.</summary>
    public void RefreshHotkeyStatus()
    {
        HotkeyStatus = HotkeyService.LastRegistrationSucceeded switch
        {
            true => LocalizationService.Get("Settings.Hotkey.Ok"),
            false => string.Format(LocalizationService.Get("Settings.Hotkey.Conflict"), HotkeyDisplayText),
            _ => string.Empty,
        };
    }
    public int HotkeyModifiers { get => _hotkeyModifiers; set { if (SetField(ref _hotkeyModifiers, value)) OnPropertyChanged(nameof(HotkeyDisplayText)); } }
    public int HotkeyVirtualKey { get => _hotkeyVirtualKey; set { if (SetField(ref _hotkeyVirtualKey, value)) OnPropertyChanged(nameof(HotkeyDisplayText)); } }
    public bool IsFlexMode { get => _displayMode == DisplayMode.Flex; set { if (value) DisplayMode = DisplayMode.Flex; } }
    public bool IsMiniMode { get => _displayMode == DisplayMode.Mini; set { if (value) DisplayMode = DisplayMode.Mini; } }
    public bool IsNoneMode { get => _displayMode == DisplayMode.None; set { if (value) DisplayMode = DisplayMode.None; } }
    public bool IsNormalMode { get => _displayMode == DisplayMode.Normal; set { if (value) DisplayMode = DisplayMode.Normal; } }
    public string Language { get => _language; set => SetField(ref _language, value); }
    public bool LunchBreakEnabled { get => _lunchBreakEnabled; set => SetField(ref _lunchBreakEnabled, value); }
    public TimeOnly LunchBreakEnd { get => _lunchBreakEnd; set => SetField(ref _lunchBreakEnd, value); }
    public TimeOnly LunchBreakStart { get => _lunchBreakStart; set => SetField(ref _lunchBreakStart, value); }
    public string MilestoneAmountText { get => _milestoneAmountText; set { SetField(ref _milestoneAmountText, value); Revalidate(); } }
    public double Opacity { get => _opacity; set => SetField(ref _opacity, Math.Clamp(value, 0.1, 1.0)); }
    public int RefreshInterval { get => _refreshInterval; set => SetField(ref _refreshInterval, Math.Clamp(value, 1, 60)); }
    public bool RunAtStartup { get => _runAtStartup; set => SetField(ref _runAtStartup, value); }
    public ICommand SaveCommand { get; }
    public string Theme { get => _theme; set => SetField(ref _theme, value); }
    public TimeOnly WorkEnd { get => _workEnd; set => SetField(ref _workEnd, value); }
    public TimeOnly WorkStart { get => _workStart; set => SetField(ref _workStart, value); }

    // ── Update properties ──────────────────────────────────────────────────
    public string CurrentVersion => AppVersion.Current;
    public string UpdateStatusText { get => _updateStatusText; private set => SetField(ref _updateStatusText, value); }
    public string UpdateReleaseNotes { get => _updateReleaseNotes; private set => SetField(ref _updateReleaseNotes, value); }
    public int DownloadProgress { get => _downloadProgress; private set => SetField(ref _downloadProgress, value); }
    public bool IsChecking { get => _isChecking; private set { if (SetField(ref _isChecking, value)) NotifyUpdateUiStateChanged(); } }
    public bool IsDownloading { get => _isDownloading; private set { if (SetField(ref _isDownloading, value)) NotifyUpdateUiStateChanged(); } }
    public ICommand CheckUpdateCommand { get; }
    public ICommand DownloadInstallCommand { get; }

    string IDataErrorInfo.this[string columnName] => columnName switch
    {
        nameof(AmountText) => ValidateAmount() ?? string.Empty,
        nameof(EndOfDayReminderMinutesText) => EnableEndOfDayReminder ? ValidateEndOfDayReminderMinutes() ?? string.Empty : string.Empty,
        nameof(MilestoneAmountText) => EnableMilestoneNotifications ? ValidateMilestoneAmount() ?? string.Empty : string.Empty,
        _ => string.Empty,
    };

    // ── Update logic ───────────────────────────────────────────────────────

    public bool HasUpdateAvailable
    {
        get => _hasUpdateAvailable;
        private set { if (SetField(ref _hasUpdateAvailable, value)) NotifyUpdateUiStateChanged(); }
    }

    public bool CanCheckUpdate => !_isChecking && !_isDownloading;
    public bool CanDownloadInstall => _hasUpdateAvailable && !_isDownloading && !_isChecking;

    /// <summary>Single helper to notify all derived update UI states after any base state change.</summary>
    private void NotifyUpdateUiStateChanged()
    {
        OnPropertyChanged(nameof(CanCheckUpdate));
        OnPropertyChanged(nameof(CanDownloadInstall));
        ((RelayCommand)CheckUpdateCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DownloadInstallCommand).RaiseCanExecuteChanged();
    }

    public async Task CheckUpdateAsync()
    {
        if (_isChecking || _isDownloading) return;
        IsChecking = true;
        UpdateStatusText = LocalizationService.Get("Settings.Update.Checking");
        UpdateReleaseNotes = "";
        HasUpdateAvailable = false;
        _pendingDownloadUrl = null;
        _pendingSha256 = null;
        try
        {
            var result = await _updateService.CheckForUpdateAsync(AppVersion.Current);
            switch (result.Status)
            {
                case UpdateService.UpdateCheckStatus.Available:
                    HasUpdateAvailable = true;
                    _pendingDownloadUrl = result.DownloadUrl;
                    _pendingSha256 = result.Sha256Digest;
                    UpdateStatusText = string.Format(LocalizationService.Get("Settings.Update.Available"), result.RemoteVersion);
                    UpdateReleaseNotes = result.ReleaseNotes ?? "";
                    _mainVm?.NotifyUpdateAvailable(result.RemoteVersion!);
                    break;
                case UpdateService.UpdateCheckStatus.UpToDate:
                    UpdateStatusText = LocalizationService.Get("Settings.Update.UpToDate");
                    break;
                case UpdateService.UpdateCheckStatus.Error:
                    UpdateStatusText = LocalizationService.Get("Settings.Update.Error");
                    break;
            }
        }
        catch
        {
            UpdateStatusText = LocalizationService.Get("Settings.Update.Error");
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task DownloadInstallAsync()
    {
        if (_isChecking || _isDownloading || _pendingDownloadUrl is null) return;
        IsDownloading = true;
        DownloadProgress = 0;
        UpdateStatusText = string.Format(LocalizationService.Get("Settings.Update.Downloading"), "");
        try
        {
            var path = await _updateService.DownloadInstallerAsync(
                _pendingDownloadUrl,
                progress => DownloadProgress = progress);

            if (path is null)
            {
                UpdateStatusText = LocalizationService.Get("Settings.Update.Error");
                HasUpdateAvailable = true; // Allow retry
                return;
            }

            // SHA256 verification — mandatory.
            if (_pendingSha256 is null || !UpdateService.VerifySha256(path, _pendingSha256))
            {
                try { File.Delete(path); } catch { }
                UpdateStatusText = LocalizationService.Get("Settings.Update.VerifyFailed");
                HasUpdateAvailable = false; // Digest mismatch — must re-check
                return;
            }

            // Launch installer helper.
            if (_updateService.LaunchInstallerAfterExit(path))
            {
                if (Application.Current is not null)
                    Application.Current.Shutdown();
            }
            else
            {
                UpdateStatusText = LocalizationService.Get("Settings.Update.Error");
            }
        }
        catch
        {
            UpdateStatusText = LocalizationService.Get("Settings.Update.Error");
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Called from App startup for auto-check. Non-blocking, fires tray notification only.</summary>
    public async Task AutoCheckUpdateAsync()
    {
        try
        {
            var result = await _updateService.CheckForUpdateAsync(AppVersion.Current);
            if (result.Status == UpdateService.UpdateCheckStatus.Available)
            {
                // Marshal notification to UI thread.
                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    dispatcher.Invoke(() => _mainVm?.NotifyUpdateAvailable(result.RemoteVersion!));
                }
                else
                {
                    _mainVm?.NotifyUpdateAvailable(result.RemoteVersion!);
                }
            }
        }
        catch
        {
            // Silent — auto-check must never break core flow.
        }
    }

    private bool CanSave() => Validate() is null;

    private void CloseWindow()
    {
        // Application.Current is null in unit tests — cancelling there must be a no-op, not a crash.
        if (Application.Current is null)
        {
            return;
        }

        foreach (Window w in Application.Current.Windows)
        {
            if (w is SettingsWindow) { w.Close(); break; }
        }
    }

    private void OpenScheduleManager()
    {
        var win = new ScheduleManagerWindow(_store, _draft, _mainVm) { Owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault() };
        win.ShowDialog();
        RefreshFromDraft();
    }

    public void RefreshFromDraft()
    {
        _config = _draft.BuildPreviewConfiguration(_store.PayData);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var schedule = _config.ResolveSchedule(today);
        _workStart = schedule.WorkStart;
        _workEnd = schedule.WorkEnd;
        _lunchBreakEnabled = schedule.LunchBreakEnabled;
        _lunchBreakStart = schedule.LunchBreakStart;
        _lunchBreakEnd = schedule.LunchBreakEnd;
        // Keep the name field in sync too: after activating another schedule in the manager,
        // the salary page must show (and re-save) that schedule's name — not the stale one.
        _scheduleName = string.IsNullOrWhiteSpace(schedule.Name)
            ? LocalizationService.Get("Salary.DefaultScheduleName")
            : schedule.Name;
        OnPropertyChanged(nameof(WorkStart));
        OnPropertyChanged(nameof(WorkEnd));
        OnPropertyChanged(nameof(LunchBreakEnabled));
        OnPropertyChanged(nameof(LunchBreakStart));
        OnPropertyChanged(nameof(LunchBreakEnd));
        OnPropertyChanged(nameof(ScheduleName));
        OnPropertyChanged(nameof(CurrentScheduleName));
        _draft.RaiseChanged();
        Revalidate();
    }

    private void ApplyWeekPreset(WorkWeekType type)
    {
        _workDays.Clear();
        foreach (var day in WorkWeekPolicy.Create(type, new DateOnly(2000, 1, 1)).WorkDays) _workDays.Add(day);
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
        if (isWorkDay) _workDays.Add(day); else _workDays.Remove(day);
        ApplyWeekToDraft();
        OnPropertyChanged(day switch { DayOfWeek.Monday => nameof(WorkMonday), DayOfWeek.Tuesday => nameof(WorkTuesday), DayOfWeek.Wednesday => nameof(WorkWednesday), DayOfWeek.Thursday => nameof(WorkThursday), DayOfWeek.Friday => nameof(WorkFriday), DayOfWeek.Saturday => nameof(WorkSaturday), _ => nameof(WorkSunday) });
    }

    /// <summary>
    /// Writes the current week edit state into the shared draft as an effective-today policy
    /// (upsert, same-day replace) so live previews (calendar page) see unsaved edits.
    /// </summary>
    private void ApplyWeekToDraft()
    {
        var effectiveDate = ResolveEffectiveDate();
        var policy = new WorkWeekPolicy { Type = _weekType, WorkDays = new HashSet<DayOfWeek>(_workDays), EffectiveFrom = effectiveDate };
        var policies = new List<WorkWeekPolicy>(_draft.WeekPolicies);
        policies.RemoveAll(p => p.EffectiveFrom >= effectiveDate);
        policies = ProfileVersioning.Upsert(policies, policy, p => p.EffectiveFrom, (a, b) => a.Type == b.Type && a.WorkDays.SetEquals(b.WorkDays));
        if (policies.Count == 0)
        {
            policies.Add(policy with { EffectiveFrom = new DateOnly(2000, 1, 1) });
        }
        _draft.WeekPolicies = policies;
        _draft.RaiseChanged();
    }

    private void Revalidate()
    {
        ErrorMessage = ValidateSchedule() ?? string.Empty;
        ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
    }

    private void Save()
    {
        if (Validate() is not null) { ErrorMessage = ValidateSchedule() ?? string.Empty; return; }

        var existing = _draft.Base;
        var today = DateOnly.FromDateTime(DateTime.Now);
        // Salary amount and work policy follow the user-selected effective date (default:
        // first day of the current month). Schedules keep their own "today" semantics.
        var effectiveDate = ResolveEffectiveDate();
        var amount = Math.Round(decimal.Parse(_amountText), 2);

        var profiles = new List<SalaryProfile>(existing.SalaryProfiles);
        var latestProfile = profiles.Count > 0 ? profiles.OrderByDescending(p => p.EffectiveFrom).First() : null;
        var desiredProfile = new SalaryProfile { Mode = _salaryMode, MonthlyAmount = _salaryMode == SalaryMode.Monthly ? amount : 0m, DailyAmount = _salaryMode == SalaryMode.Daily ? amount : 0m, EffectiveFrom = effectiveDate };
        if (profiles.Count == 0)
        {
            profiles.Add(desiredProfile with { EffectiveFrom = new DateOnly(2000, 1, 1) });
        }
        else
        {
            // "从 X 日起生效" supersedes EVERY version dated X or later — otherwise an older
            // save with a later effective date (e.g. 单休@08-29) would keep outranking the
            // new rule (双休@08-01) when resolving "today". Versions before X stay intact.
            profiles.RemoveAll(p => p.EffectiveFrom >= effectiveDate);
            profiles = ProfileVersioning.Upsert(profiles, desiredProfile, p => p.EffectiveFrom, (a, b) => a.Mode == b.Mode && a.MonthlyAmount == b.MonthlyAmount && a.DailyAmount == b.DailyAmount);
        }

        var schedules = new List<WorkScheduleProfile>(existing.ScheduleProfiles);
        var latestSchedule = schedules.Count > 0 ? schedules.OrderByDescending(s => s.EffectiveFrom).First() : null;
        var desiredSchedule = new WorkScheduleProfile { Id = latestSchedule?.EffectiveFrom == today ? latestSchedule.Id : Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(_scheduleName) ? LocalizationService.Get("Salary.DefaultScheduleName") : _scheduleName.Trim(), WorkStart = _workStart, WorkEnd = _workEnd, LunchBreakEnabled = _lunchBreakEnabled, LunchBreakStart = _lunchBreakStart, LunchBreakEnd = _lunchBreakEnd, EffectiveFrom = today };
        if (latestSchedule is null) schedules.Add(desiredSchedule with { EffectiveFrom = new DateOnly(2000, 1, 1) });
        else if (latestSchedule.EffectiveFrom == today) schedules[schedules.IndexOf(latestSchedule)] = desiredSchedule;
        else if (!ScheduleEquals(latestSchedule, desiredSchedule)) schedules.Add(desiredSchedule);

        var policies = new List<WorkWeekPolicy>(existing.WeekPolicies);
        var latestPolicy = policies.Count > 0 ? policies.OrderByDescending(p => p.EffectiveFrom).First() : null;
        var desiredPolicy = new WorkWeekPolicy { Type = _weekType, WorkDays = new HashSet<DayOfWeek>(_workDays), EffectiveFrom = effectiveDate };
        if (policies.Count == 0)
        {
            policies.Add(desiredPolicy with { EffectiveFrom = new DateOnly(2000, 1, 1) });
        }
        else
        {
            policies.RemoveAll(p => p.EffectiveFrom >= effectiveDate);
            policies = ProfileVersioning.Upsert(policies, desiredPolicy, p => p.EffectiveFrom, (a, b) => a.Type == b.Type && a.WorkDays.SetEquals(b.WorkDays));
        }

        profiles = ProfileVersioning.DeduplicateByDate(profiles, p => p.EffectiveFrom);
        schedules = ProfileVersioning.DeduplicateByDate(schedules, s => s.EffectiveFrom);
        policies = ProfileVersioning.DeduplicateByDate(policies, p => p.EffectiveFrom);

        var settings = existing with
        {
            DisplayMode = DisplayMode, AlwaysOnTop = AlwaysOnTop, Opacity = Opacity, RefreshInterval = RefreshInterval,
            Language = Language, Theme = Theme, HotkeyModifiers = HotkeyModifiers, HotkeyVirtualKey = HotkeyVirtualKey,
            EnableEndOfDayReminder = EnableEndOfDayReminder,
            EndOfDayReminderMinutes = EnableEndOfDayReminder && int.TryParse(_endOfDayReminderMinutesText, out var parsedMinutes) ? parsedMinutes : existing.EndOfDayReminderMinutes,
            EnableMilestoneNotifications = EnableMilestoneNotifications,
            MilestoneAmount = EnableMilestoneNotifications && decimal.TryParse(_milestoneAmountText, out var parsedMilestone) ? Math.Round(parsedMilestone, 2) : existing.MilestoneAmount,
            SalaryProfiles = profiles, ScheduleProfiles = schedules, WeekPolicies = policies, SetupCompleted = true,
        };

        _store.Commit(settings);
        if (_runAtStartup != _originalRunAtStartup)
        {
            // Only touch the registry when the user actually toggled the checkbox — avoids
            // rewriting (and in tests, clobbering) the Run entry on every save.
            StartupService.SetEnabled(_runAtStartup);
            _originalRunAtStartup = _runAtStartup;
        }
        LocalizationService.Apply(Language);
        ThemeService.Apply(Theme);
        RefreshHotkeyStatus();
        CloseWindow();
    }

    private static bool ScheduleEquals(WorkScheduleProfile a, WorkScheduleProfile b) =>
        a.WorkStart == b.WorkStart && a.WorkEnd == b.WorkEnd && a.LunchBreakEnabled == b.LunchBreakEnabled && a.LunchBreakStart == b.LunchBreakStart && a.LunchBreakEnd == b.LunchBreakEnd;

    private string? Validate()
    {
        if (ValidateAmount() is { } ae) return ae;
        if (ValidateSchedule() is { } se) return se;
        if (EnableEndOfDayReminder && ValidateEndOfDayReminderMinutes() is { } me) return me;
        if (EnableMilestoneNotifications && ValidateMilestoneAmount() is { } ms) return ms;
        if (_workDays.Count == 0) return LocalizationService.Get("Error.WorkDayRequired");
        if (_effectiveChoice == EffectiveDateChoice.Custom
            && !DateOnly.TryParseExact(_customEffectiveDateText.Trim(), "yyyy-MM-dd", out _))
        {
            return LocalizationService.Get("Error.EffectiveDateInvalid");
        }
        return null;
    }

    private string? ValidateAmount()
    {
        if (!decimal.TryParse(_amountText, out var amount) || amount <= 0) return LocalizationService.Get("Error.SalaryPositive");
        if (amount > SalarySettings.MaxDailySalary) return LocalizationService.Get("Error.SalaryTooLarge");
        return null;
    }
    private string? ValidateEndOfDayReminderMinutes()
    {
        if (!int.TryParse(_endOfDayReminderMinutesText, out var minutes) || minutes < 1 || minutes > 60) return LocalizationService.Get("Error.EndOfDayReminderMinutesInvalid");
        return null;
    }
    private string? ValidateMilestoneAmount()
    {
        if (!decimal.TryParse(_milestoneAmountText, out var milestone) || milestone <= 0) return LocalizationService.Get("Error.MilestoneAmountPositive");
        if (decimal.TryParse(_amountText, out var daily) && milestone > daily) return LocalizationService.Get("Error.MilestoneAmountTooLarge");
        return null;
    }
    private string? ValidateSchedule()
    {
        if (WorkStart >= WorkEnd) return LocalizationService.Get("Error.WorkEndAfterStart");
        if (LunchBreakEnabled && (LunchBreakStart >= LunchBreakEnd || LunchBreakStart < WorkStart || LunchBreakEnd > WorkEnd)) return LocalizationService.Get("Error.LunchBreakInvalid");
        return null;
    }
}
