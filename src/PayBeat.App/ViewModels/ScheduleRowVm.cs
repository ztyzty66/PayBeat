using PayBeat.App.Domain;
using PayBeat.App.Services;

namespace PayBeat.App.ViewModels;

/// <summary>
/// Presentation row for the schedule-manager list. Gives the ListBox a real DataTemplate
/// payload (name, work window, lunch, effective date, active state) so the control never
/// falls back to <see cref="object.ToString"/> and leaks a namespace/type name.
/// </summary>
public class ScheduleRowVm
{
    /// <summary>Creates the row from a schedule profile, whether it is the current one, and an
    /// optional localization badge key (unsaved / pending) for newly drafted schedules.</summary>
    public ScheduleRowVm(WorkScheduleProfile schedule, bool isActive, string? badge = null)
    {
        Schedule = schedule;
        IsActive = isActive;
        BadgeKey = badge;
    }

    /// <summary>Badge key (e.g. unsaved / pending) or <see langword="null"/> for settled rows.</summary>
    public string? BadgeKey { get; }

    /// <summary>Localized badge text; empty when the row has no badge.</summary>
    public string BadgeText => BadgeKey is null ? string.Empty : LocalizationService.Get(BadgeKey);

    /// <summary>Visibility hint for the badge chip in XAML.</summary>
    public System.Windows.Visibility BadgeVisible => BadgeKey is null
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;

    /// <summary>The underlying schedule profile (used for editing/activation).</summary>
    public WorkScheduleProfile Schedule { get; }

    /// <summary>Schedule display name, e.g. "🌞 夏季作息".</summary>
    public string Name => string.IsNullOrWhiteSpace(Schedule.Name)
        ? LocalizationService.Get("Salary.DefaultScheduleName")
        : Schedule.Name;

    /// <summary>Work window text, e.g. "07:30 – 17:00".</summary>
    public string TimeText => $"{Schedule.WorkStart:HH:mm} – {Schedule.WorkEnd:HH:mm}";

    /// <summary>Whether lunch is deducted for this schedule.</summary>
    public bool HasLunch => Schedule.LunchSpan() is not null;

    /// <summary>Lunch range text, e.g. "11:15 – 12:45", or "—" when lunch is not deducted.
    /// The localized "午休" label is composed in the DataTemplate via DynamicResource.</summary>
    public string LunchText => Schedule.LunchSpan() is { } lunch
        ? $"{lunch.Start:HH:mm} – {lunch.End:HH:mm}"
        : "—";

    /// <summary>Raw effective date, e.g. "2026-08-29" (locale-independent, test-facing).</summary>
    public string EffectiveDateText => Schedule.EffectiveFrom.ToString("yyyy-MM-dd");

    /// <summary>Effective date text, e.g. "2026-08-29 起".</summary>
    public string EffectiveText =>
        string.Format(LocalizationService.Get("Schedule.EffectiveFromShort"), EffectiveDateText);

    /// <summary>Whether this schedule is the one effective today.</summary>
    public bool IsActive { get; }

    /// <summary>Active badge text, e.g. "✅ 当前使用" (empty when not active).</summary>
    public string ActiveText => IsActive ? LocalizationService.Get("Schedule.ActiveBadge") : string.Empty;

    /// <summary>Visibility hint for the active badge in XAML.</summary>
    public System.Windows.Visibility ActiveBadgeVisible => IsActive
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    /// <summary>Human-readable status text for tests: never falls back to a type name.</summary>
    public override string ToString() => $"{Name} {TimeText}";
}
