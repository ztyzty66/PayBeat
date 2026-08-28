using System.Windows;
using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;

namespace PayBeat.App.Views;

/// <summary>
/// Manage work-schedule profiles: create, edit, delete (except the schedule currently in effect),
/// and set effective dates. Editing a past-effective schedule creates a new version effective
/// today instead of rewriting it, keeping month history intact.
/// </summary>
public partial class ScheduleManagerWindow
{
    private readonly SettingsService _settingsService;
    private readonly MainViewModel _mainVm;
    private PayConfiguration _config = null!;
    private SalarySettings _settings = null!;
    private WorkScheduleProfile? _selected;
    private bool _isNewEntry;

    /// <summary>Builds the manager over the given settings service.</summary>
    public ScheduleManagerWindow(SettingsService settingsService, MainViewModel mainVm)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _mainVm = mainVm;
        Owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        Reload();
    }

    private void Reload()
    {
        _settings = _settingsService.Load();
        _config = new PayDataService(_settingsService, new HistoryService()).BuildConfiguration(_settings);
        var activeId = _config.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now)).Id;

        ScheduleList.ItemsSource = _settings.ScheduleProfiles
            .OrderByDescending(s => s.EffectiveFrom)
            .Select(s => new ScheduleRow(s, s.Id == activeId))
            .ToList();
    }

    private record ScheduleRow(WorkScheduleProfile Schedule, bool IsActive)
    {
        public string Display => $"{(string.IsNullOrWhiteSpace(Schedule.Name) ? LocalizationService.Get("Salary.DefaultScheduleName") : Schedule.Name)}"
                                 + $"  ({Schedule.EffectiveFrom:yyyy-MM-dd}){(IsActive ? " · " + LocalizationService.Get("Schedule.Active") : "")}";
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ScheduleList.SelectedItem is ScheduleRow row)
        {
            _selected = row.Schedule;
            _isNewEntry = false;
            LoadForm(row.Schedule);
        }
    }

    private void LoadForm(WorkScheduleProfile schedule)
    {
        NameBox.Text = string.IsNullOrWhiteSpace(schedule.Name) ? LocalizationService.Get("Salary.DefaultScheduleName") : schedule.Name;
        StartTime.SelectedTime = schedule.WorkStart;
        EndTime.SelectedTime = schedule.WorkEnd;
        LunchCheck.IsChecked = schedule.LunchBreakEnabled;
        LunchStartTime.SelectedTime = schedule.LunchBreakStart;
        LunchEndTime.SelectedTime = schedule.LunchBreakEnd;
        EffectiveFromBox.Text = schedule.EffectiveFrom.ToString("yyyy-MM-dd");
        DeleteButton.IsEnabled = schedule.Id != _config.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now)).Id;
        FormError.Text = DeleteButton.IsEnabled ? "" : LocalizationService.Get("Schedule.DeleteDisabled");
    }

    private void ClearForm()
    {
        _selected = null;
        _isNewEntry = true;
        NameBox.Text = "";
        StartTime.SelectedTime = new TimeOnly(9, 0);
        EndTime.SelectedTime = new TimeOnly(18, 0);
        LunchCheck.IsChecked = false;
        LunchStartTime.SelectedTime = new TimeOnly(12, 0);
        LunchEndTime.SelectedTime = new TimeOnly(13, 0);
        EffectiveFromBox.Text = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        DeleteButton.IsEnabled = false;
        FormError.Text = "";
        NameBox.Focus();
    }

    private void OnNew(object sender, RoutedEventArgs e) => ClearForm();

    private void OnSaveSchedule(object sender, RoutedEventArgs e)
    {
        FormError.Text = "";

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            FormError.Text = LocalizationService.Get("Error.ScheduleNameRequired");
            return;
        }
        if (!DateOnly.TryParseExact(EffectiveFromBox.Text, "yyyy-MM-dd", out var effectiveFrom))
        {
            FormError.Text = LocalizationService.Get("Error.ScheduleOverlap");
            return;
        }

        var start = StartTime.SelectedTime;
        var end = EndTime.SelectedTime;
        var lunchOn = LunchCheck.IsChecked == true;
        var lunchStart = LunchStartTime.SelectedTime;
        var lunchEnd = LunchEndTime.SelectedTime;
        if (start >= end
            || (lunchOn && (lunchStart >= lunchEnd || lunchStart < start || lunchEnd > end)))
        {
            FormError.Text = LocalizationService.Get("Error.LunchBreakInvalid");
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var schedules = new List<WorkScheduleProfile>(_settings.ScheduleProfiles);
        var active = _config.ResolveSchedule(today);

        if (_isNewEntry || _selected is null)
        {
            schedules.Add(new WorkScheduleProfile
            {
                Name = name,
                WorkStart = start,
                WorkEnd = end,
                LunchBreakEnabled = lunchOn,
                LunchBreakStart = lunchStart,
                LunchBreakEnd = lunchEnd,
                EffectiveFrom = effectiveFrom,
            });
        }
        else
        {
            var index = schedules.FindIndex(s => s.Id == _selected.Id);
            if (index < 0)
            {
                return;
            }

            var edited = schedules[index];
            var sameId = edited.Id;
            if (edited.EffectiveFrom < today)
            {
                // Never rewrite history: create a new version effective from the requested date.
                sameId = Guid.NewGuid().ToString("N");
                schedules.Add(new WorkScheduleProfile
                {
                    Id = sameId,
                    Name = name,
                    WorkStart = start,
                    WorkEnd = end,
                    LunchBreakEnabled = lunchOn,
                    LunchBreakStart = lunchStart,
                    LunchBreakEnd = lunchEnd,
                    EffectiveFrom = effectiveFrom,
                });
            }
            else
            {
                schedules[index] = edited with
                {
                    Name = name,
                    WorkStart = start,
                    WorkEnd = end,
                    LunchBreakEnabled = lunchOn,
                    LunchBreakStart = lunchStart,
                    LunchBreakEnd = lunchEnd,
                    EffectiveFrom = effectiveFrom,
                };
            }
        }

        _settingsService.Save(_settings with { ScheduleProfiles = schedules });
        _mainVm.ReloadSettings();
        Reload();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var activeId = _config.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now)).Id;
        if (_selected.Id == activeId)
        {
            FormError.Text = LocalizationService.Get("Schedule.DeleteDisabled");
            return;
        }

        var schedules = _settings.ScheduleProfiles.Where(s => s.Id != _selected.Id).ToList();
        _settingsService.Save(_settings with { ScheduleProfiles = schedules });
        _mainVm.ReloadSettings();
        Reload();
        ClearForm();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnLunchChanged(object sender, RoutedEventArgs e)
    {
        // Only used to refresh enable-state through IsEnabled binding.
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
