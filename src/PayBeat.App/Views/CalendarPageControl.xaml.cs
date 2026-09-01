using PayBeat.App.ViewModels;

namespace PayBeat.App.Views;

/// <summary>
/// Calendar page: month grid of day statuses; clicking a day opens the day editor dialog.
/// </summary>
public partial class CalendarPageControl
{
    /// <summary>Builds the control; the DataContext must be a <see cref="CalendarViewModel"/>.</summary>
    public CalendarPageControl()
    {
        InitializeComponent();
    }

    private CalendarViewModel Vm => (CalendarViewModel)DataContext;

    // TabControl unloads non-selected tab content: detach while hidden, re-attach (and
    // re-sync today) when the calendar page comes back.
    private void OnLoaded(object sender, RoutedEventArgs e) => (DataContext as CalendarViewModel)?.Attach();

    private void OnUnloaded(object sender, RoutedEventArgs e) => (DataContext as CalendarViewModel)?.Detach();

    private void OnPrevMonth(object sender, RoutedEventArgs e) => Vm.PreviousMonth();

    private void OnNextMonth(object sender, RoutedEventArgs e) => Vm.NextMonth();

    private void OnToday(object sender, RoutedEventArgs e) => Vm.GoToToday();

    private void OnDayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is CalendarDayVm day)
        {
            Vm.EditDay(day);
        }
    }
}
