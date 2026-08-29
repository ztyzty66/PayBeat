using System.Windows;
using PayBeat.App.Domain;
using PayBeat.App.Services;

namespace PayBeat.App.Views;

/// <summary>
/// Modal editor for one calendar day: pick a status (work/rest/holiday/makeup/PTO/leave) and,
/// for leave, the granularity plus an optional hour range. <see cref="Result"/> carries the
/// outcome back to <see cref="ViewModels.CalendarViewModel"/>. Radio-group state is plain
/// view state, so it is managed directly here.
/// </summary>
public partial class DayEditorWindow
{
    /// <summary>Outcome of the dialog: Clear=true removes any override for the day.</summary>
    public record EditorResult(bool Clear, CalendarOverride Override);

    private readonly DateOnly _date;

    /// <summary>Result after the dialog closes; <see langword="null"/> when cancelled.</summary>
    public EditorResult? Result { get; private set; }

    /// <summary>Builds the editor for <paramref name="date"/> pre-filled from <paramref name="existing"/>.
    /// <paramref name="config"/> supplies the resolved status and its source (manual override,
    /// holiday dataset, weekly policy or default rule) for the transparency callout.</summary>
    public DayEditorWindow(DateOnly date, CalendarOverride? existing, PayConfiguration config)
    {
        InitializeComponent();
        _date = date;
        EditorTitle.Text = string.Format(LocalizationService.Get("Calendar.DayEditor"), date.Month, date.Day);
        Owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();

        var status = config.ResolveDayStatus(date);
        var source = config.ResolveDayStatusSource(date);
        CurrentStatusText.Text = LocalizationService.Get(StatusKeyOf(status));
        SourceText.Text = LocalizationService.Get(SourceKeyOf(source));
        ClearButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        if (source == DayStatusSource.ManualOverride)
        {
            ManualBadge.Visibility = Visibility.Visible;
        }

        if (existing is null)
        {
            return;
        }

        SelectStatus(existing.Status);
        if (existing.Leave is { } leave)
        {
            SelectLeaveKind(leave.Kind, leave.Start, leave.End);
        }
    }

    private static string StatusKeyOf(DayStatus status) => status switch
    {
        DayStatus.Rest => "Calendar.Status.Rest",
        DayStatus.PublicHoliday => "Calendar.Status.Holiday",
        DayStatus.MakeupWork => "Calendar.Status.Makeup",
        DayStatus.PaidTimeOff => "Calendar.Status.Pto",
        DayStatus.Leave => "Calendar.Status.Leave",
        _ => "Calendar.Status.Normal",
    };

    private static string SourceKeyOf(DayStatusSource source) => source switch
    {
        DayStatusSource.ManualOverride => "Calendar.Source.ManualOverride",
        DayStatusSource.PublicHoliday => "Calendar.Source.PublicHoliday",
        DayStatusSource.MakeupWork => "Calendar.Source.MakeupWork",
        DayStatusSource.DefaultRule => "Calendar.Source.DefaultRule",
        _ => "Calendar.Source.WeekPolicy",
    };

    private void SelectStatus(DayStatus status)
    {
        StatusWork.IsChecked = status == DayStatus.Work;
        StatusRest.IsChecked = status == DayStatus.Rest;
        StatusHoliday.IsChecked = status == DayStatus.PublicHoliday;
        StatusMakeup.IsChecked = status == DayStatus.MakeupWork;
        StatusPto.IsChecked = status == DayStatus.PaidTimeOff;
        StatusLeave.IsChecked = status == DayStatus.Leave;
        UpdateLeaveVisibility();
    }

    private void SelectLeaveKind(LeaveKind kind, TimeOnly start, TimeOnly end)
    {
        LeaveFull.IsChecked = kind == LeaveKind.FullDay;
        LeaveMorning.IsChecked = kind == LeaveKind.Morning;
        LeaveAfternoon.IsChecked = kind == LeaveKind.Afternoon;
        LeaveHoursOption.IsChecked = kind == LeaveKind.Hours;
        if (kind == LeaveKind.Hours)
        {
            LeaveStart.SelectedTime = start;
            LeaveEnd.SelectedTime = end;
        }
        UpdateLeaveVisibility();
    }

    private DayStatus SelectedStatus =>
        StatusRest.IsChecked == true ? DayStatus.Rest
        : StatusHoliday.IsChecked == true ? DayStatus.PublicHoliday
        : StatusMakeup.IsChecked == true ? DayStatus.MakeupWork
        : StatusPto.IsChecked == true ? DayStatus.PaidTimeOff
        : StatusLeave.IsChecked == true ? DayStatus.Leave
        : DayStatus.Work;

    private LeaveKind SelectedLeaveKind =>
        LeaveMorning.IsChecked == true ? LeaveKind.Morning
        : LeaveAfternoon.IsChecked == true ? LeaveKind.Afternoon
        : LeaveHoursOption.IsChecked == true ? LeaveKind.Hours
        : LeaveKind.FullDay;

    private void UpdateLeaveVisibility()
    {
        // XAML parsing raises Checked while later sibling controls are still null — wait until loaded.
        if (LeavePanel is null || LeaveHoursPanel is null || LeaveHoursOption is null || StatusLeave is null)
        {
            return;
        }
        var isLeave = StatusLeave.IsChecked == true;
        LeavePanel.Visibility = isLeave ? Visibility.Visible : Visibility.Collapsed;
        LeaveHoursPanel.Visibility = isLeave && LeaveHoursOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStatusChanged(object sender, System.Windows.RoutedEventArgs e) => UpdateLeaveVisibility();

    private void OnLeaveKindChanged(object sender, RoutedEventArgs e) => UpdateLeaveVisibility();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var status = SelectedStatus;
        if (status == DayStatus.Leave)
        {
            var kind = SelectedLeaveKind;
            var leave = kind == LeaveKind.Hours
                ? new LeaveRecord(LeaveKind.Hours, LeaveStart.SelectedTime, LeaveEnd.SelectedTime)
                : new LeaveRecord(kind);
            Result = new EditorResult(false, CalendarOverride.LeaveOverride(_date, leave));
        }
        else
        {
            Result = new EditorResult(false, CalendarOverride.For(_date, status));
        }

        Close();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Result = new EditorResult(true, CalendarOverride.For(_date, DayStatus.Work));
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
