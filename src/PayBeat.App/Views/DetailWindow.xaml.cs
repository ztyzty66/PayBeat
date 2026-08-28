using System.Windows;
using PayBeat.App.Domain;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;

namespace PayBeat.App.Views;

/// <summary>
/// Detail dashboard: "Today" tab mirrors the live view-model; "Month" tab lists history months
/// from immutable snapshots and shows the frozen per-month aggregates and configuration used.
/// </summary>
public partial class DetailWindow
{
    private readonly PayDataService _payData;
    private readonly MainViewModel _mainVm;

    /// <summary>Builds the window; <paramref name="mainVm"/> supplies live values for the Today tab.</summary>
    public DetailWindow(PayDataService payData, MainViewModel mainVm)
    {
        InitializeComponent();
        _payData = payData;
        _mainVm = mainVm;
        DataContext = mainVm;
        LoadMonthList();
    }

    private void LoadMonthList()
    {
        var months = _payData.History.ListMonths();
        HistoryEmpty.Visibility = months.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MonthList.ItemsSource = months;
    }

    private void OnMonthClick(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is DateOnly month)
        {
            ShowHistory(month);
        }
    }

    /// <summary>Renders the frozen snapshot of one history month.</summary>
    private void ShowHistory(DateOnly month)
    {
        var history = _payData.History.Load(month);
        if (history is null)
        {
            return;
        }

        HistoryEmpty.Visibility = Visibility.Collapsed;
        HistoryDetail.Visibility = Visibility.Visible;

        HistStandard.Text = history.StandardMonthlySnapshot.ToString("N2");
        HistTarget.Text = history.MonthTargetSnapshot.ToString("N2");
        HistEarned.Text = history.MonthEarnedSnapshot.ToString("N2");
        HistWorkdays.Text = $"{history.Days.Count} / {history.PlannedWorkdays}";

        var scheduleNames = history.Days.Values
            .Select(d => d.ScheduleSnapshot?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();
        HistSchedule.Text = scheduleNames.Count > 0
            ? string.Join(" / ", scheduleNames)
            : LocalizationService.Get("Salary.DefaultScheduleName");

        HistPolicy.Text = history.WorkWeekTypeSnapshot switch
        {
            nameof(WorkWeekType.DoubleRest) => LocalizationService.Get("Settings.Week.Double"),
            nameof(WorkWeekType.SingleRest) => LocalizationService.Get("Settings.Week.Single"),
            nameof(WorkWeekType.Custom) => LocalizationService.Get("Settings.Week.Custom"),
            _ => history.WorkWeekTypeSnapshot,
        };

        var statusKeys = new Dictionary<DayStatus, string>
        {
            [DayStatus.Rest] = "Calendar.Legend.Rest",
            [DayStatus.PublicHoliday] = "Calendar.Legend.Holiday",
            [DayStatus.MakeupWork] = "Calendar.Legend.Makeup",
            [DayStatus.PaidTimeOff] = "Calendar.Legend.Pto",
            [DayStatus.Leave] = "Calendar.Legend.Leave",
        };
        SpecialDaysList.ItemsSource = history.Days.Values
            .Where(d => d.Status is DayStatus.Rest or DayStatus.PublicHoliday or DayStatus.MakeupWork
                or DayStatus.PaidTimeOff or DayStatus.Leave)
            .OrderBy(d => d.Date)
            .Select(d =>
            {
                var label = statusKeys.TryGetValue(d.Status, out var key)
                    ? LocalizationService.Get(key)
                    : "";
                return $"{d.Date:MM-dd} {label}";
            })
            .ToList();
    }

    private void OnTodayTab(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" fires during XAML parsing before the views exist.
        if (TodayView is null || MonthView is null)
        {
            return;
        }
        TodayView.Visibility = Visibility.Visible;
        MonthView.Visibility = Visibility.Collapsed;
    }

    private void OnMonthTab(object sender, RoutedEventArgs e)
    {
        if (TodayView is null || MonthView is null)
        {
            return;
        }
        TodayView.Visibility = Visibility.Collapsed;
        MonthView.Visibility = Visibility.Visible;
        LoadMonthList();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
