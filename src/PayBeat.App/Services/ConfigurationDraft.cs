using PayBeat.App.Domain;
using PayBeat.App.Models;

namespace PayBeat.App.Services;

/// <summary>
/// Mutable snapshot of <see cref="SalarySettings"/> used as a shared draft while the
/// SettingsWindow is open. All child editors (salary, schedule, calendar, day editor)
/// operate on this single draft. Only a successful Save from the main SettingsWindow
/// calls <see cref="ConfigurationStore.Commit"/> to persist and propagate changes.
/// </summary>
public class ConfigurationDraft
{
    private SalarySettings _base;

    public ConfigurationDraft(SalarySettings baseSettings)
    {
        _base = baseSettings.DeepClone();
    }

    /// <summary>
    /// Raised when a draft section changes in a way that live previews (calendar page) must
    /// re-render. Subscribers share the draft's lifetime (both are owned by the settings
    /// window), so no explicit unsubscribe is required.
    /// </summary>
    public event Action? Changed;

    /// <summary>Raises <see cref="Changed"/> after a draft mutation.</summary>
    public void RaiseChanged() => Changed?.Invoke();

    /// <summary>Returns a deep snapshot of the underlying settings. Callers must not
    /// mutate the returned object — use the property setters instead.</summary>
    public SalarySettings Base => _base.DeepClone();

    /// <summary>Current salary profiles. Returns a defensive copy to prevent in-place mutation.</summary>
    public List<SalaryProfile> SalaryProfiles
    {
        get => new(_base.SalaryProfiles);
        set => _base = _base with { SalaryProfiles = value };
    }

    /// <summary>Current schedule profiles. Returns a defensive copy to prevent in-place mutation.</summary>
    public List<WorkScheduleProfile> ScheduleProfiles
    {
        get => new(_base.ScheduleProfiles);
        set => _base = _base with { ScheduleProfiles = value };
    }

    /// <summary>Current week policies. Returns a defensive copy to prevent in-place mutation.</summary>
    public List<WorkWeekPolicy> WeekPolicies
    {
        get => new(_base.WeekPolicies);
        set => _base = _base with { WeekPolicies = value };
    }

    /// <summary>Per-date overrides. Returns a defensive copy to prevent in-place mutation.</summary>
    public Dictionary<string, CalendarOverride> Overrides
    {
        get => new(_base.Overrides);
        set => _base = _base with { Overrides = value };
    }

    /// <summary>Display mode.</summary>
    public DisplayMode DisplayMode
    {
        get => _base.DisplayMode;
        set => _base = _base with { DisplayMode = value };
    }

    /// <summary>Always on top.</summary>
    public bool AlwaysOnTop
    {
        get => _base.AlwaysOnTop;
        set => _base = _base with { AlwaysOnTop = value };
    }

    /// <summary>Window opacity.</summary>
    public double Opacity
    {
        get => _base.Opacity;
        set => _base = _base with { Opacity = value };
    }

    /// <summary>Refresh interval.</summary>
    public int RefreshInterval
    {
        get => _base.RefreshInterval;
        set => _base = _base with { RefreshInterval = value };
    }

    /// <summary>Language code.</summary>
    public string Language
    {
        get => _base.Language;
        set => _base = _base with { Language = value };
    }

    /// <summary>Theme code.</summary>
    public string Theme
    {
        get => _base.Theme;
        set => _base = _base with { Theme = value };
    }

    /// <summary>Hotkey modifiers.</summary>
    public int HotkeyModifiers
    {
        get => _base.HotkeyModifiers;
        set => _base = _base with { HotkeyModifiers = value };
    }

    /// <summary>Hotkey virtual key.</summary>
    public int HotkeyVirtualKey
    {
        get => _base.HotkeyVirtualKey;
        set => _base = _base with { HotkeyVirtualKey = value };
    }

    /// <summary>Enable end-of-day reminder.</summary>
    public bool EnableEndOfDayReminder
    {
        get => _base.EnableEndOfDayReminder;
        set => _base = _base with { EnableEndOfDayReminder = value };
    }

    /// <summary>End-of-day reminder minutes.</summary>
    public int EndOfDayReminderMinutes
    {
        get => _base.EndOfDayReminderMinutes;
        set => _base = _base with { EndOfDayReminderMinutes = value };
    }

    /// <summary>Enable milestone notifications.</summary>
    public bool EnableMilestoneNotifications
    {
        get => _base.EnableMilestoneNotifications;
        set => _base = _base with { EnableMilestoneNotifications = value };
    }

    /// <summary>Milestone amount.</summary>
    public decimal MilestoneAmount
    {
        get => _base.MilestoneAmount;
        set => _base = _base with { MilestoneAmount = value };
    }

    /// <summary>Currency symbol.</summary>
    public string Currency
    {
        get => _base.Currency;
        set => _base = _base with { Currency = value };
    }

    /// <summary>Setup completed flag.</summary>
    public bool SetupCompleted
    {
        get => _base.SetupCompleted;
        set => _base = _base with { SetupCompleted = value };
    }

    /// <summary>Legacy schedule name.</summary>
    public string LegacyScheduleName
    {
        get => _base.LegacyScheduleName;
        set => _base = _base with { LegacyScheduleName = value };
    }

    /// <summary>Returns a deep snapshot of the settings for persistence.</summary>
    public SalarySettings ToSettings() => _base.DeepClone();

    /// <summary>
    /// Replaces the underlying settings entirely (used when loading from store after
    /// schedule manager changes).
    /// </summary>
    public void ReplaceBase(SalarySettings newBase) => _base = newBase;

    /// <summary>
    /// Builds a <see cref="PayConfiguration"/> from the current draft state.
    /// Used by CalendarViewModel and other preview scenarios.
    /// </summary>
    public PayConfiguration BuildPreviewConfiguration(PayDataService payData) =>
        payData.BuildConfiguration(_base);
}
