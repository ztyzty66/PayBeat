using System.Windows;
using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;

namespace PayBeat.App.Views;

/// <summary>
/// First-run setup: one compact form that seeds the versioned profile model, marks setup complete,
/// and drops the user straight into the widget.
/// </summary>
public partial class FirstRunWindow
{
    private readonly ConfigurationStore _store;

    /// <summary>Builds the first-run window over the configuration store.</summary>
    public FirstRunWindow(ConfigurationStore store)
    {
        InitializeComponent();
        _store = store;
        StartTime.SelectedTime = new TimeOnly(9, 0);
        EndTime.SelectedTime = new TimeOnly(18, 0);
        LunchStartTime.SelectedTime = new TimeOnly(12, 0);
        LunchEndTime.SelectedTime = new TimeOnly(13, 0);
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        if (!decimal.TryParse(AmountBox.Text, out var amount) || amount <= 0 || amount > SalarySettings.MaxDailySalary)
        {
            ErrorText.Text = LocalizationService.Get("Error.SalaryPositive");
            return;
        }

        var start = StartTime.SelectedTime;
        var end = EndTime.SelectedTime;
        var lunchOn = LunchCheck.IsChecked == true;
        var lunchStart = LunchStartTime.SelectedTime;
        var lunchEnd = LunchEndTime.SelectedTime;
        if (start >= end || (lunchOn && (lunchStart >= lunchEnd || lunchStart < start || lunchEnd > end)))
        {
            ErrorText.Text = lunchOn
                ? LocalizationService.Get("Error.LunchBreakInvalid")
                : LocalizationService.Get("Error.WorkEndAfterStart");
            return;
        }

        var weekType = WeekSingle.IsChecked == true ? WorkWeekType.SingleRest
            : WeekCustom.IsChecked == true ? WorkWeekType.Custom
            : WorkWeekType.DoubleRest;
        var mode = ModeDaily.IsChecked == true ? SalaryMode.Daily : SalaryMode.Monthly;
        // Salary and work policy cover the whole current month from day one; the work
        // schedule starts today (mid-month schedule switches are the normal case).
        var today = DateOnly.FromDateTime(DateTime.Now);
        var since = new DateOnly(today.Year, today.Month, 1);
        var scheduleSince = today;
        var scheduleName = LocalizationService.Get("Salary.DefaultScheduleName");

        var existing = _store.CurrentSettings;
        var settings = existing with
        {
            ConfigVersion = 3,
            SalaryProfiles = [new SalaryProfile
            {
                Mode = mode,
                MonthlyAmount = mode == SalaryMode.Monthly ? amount : 0m,
                DailyAmount = mode == SalaryMode.Daily ? amount : 0m,
                EffectiveFrom = since,
            }],
            ScheduleProfiles = [new WorkScheduleProfile
            {
                Id = PayConfiguration.DefaultScheduleId,
                Name = scheduleName,
                WorkStart = start,
                WorkEnd = end,
                LunchBreakEnabled = lunchOn,
                LunchBreakStart = lunchStart,
                LunchBreakEnd = lunchEnd,
                EffectiveFrom = scheduleSince,
            }],
            WeekPolicies = [WorkWeekPolicy.Create(weekType, since)],
            SetupCompleted = true,
        };

        _store.Commit(settings);
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
