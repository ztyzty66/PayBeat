using System.Windows;
using System.Windows.Controls;
using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;

namespace PayBeat.App.Views;

/// <summary>
/// Manage work-schedule profiles. Operates on the shared <see cref="ConfigurationDraft"/>
/// so changes are reflected in the parent SettingsWindow without writing to disk.
/// </summary>
public partial class ScheduleManagerWindow
{
    private readonly ConfigurationStore _store;
    private readonly ConfigurationDraft _draft;
    private readonly MainViewModel _mainVm;
    private PayConfiguration _config = null!;
    private SalarySettings _settings = null!;
    private WorkScheduleProfile? _selected;
    private bool _isNewEntry;

    public ScheduleManagerWindow(ConfigurationStore store, ConfigurationDraft draft, MainViewModel mainVm)
    {
        InitializeComponent();
        _store = store;
        _draft = draft;
        _mainVm = mainVm;
        Owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        Reload();
    }

    private class ScheduleRow
    {
        public WorkScheduleProfile Schedule { get; init; } = null!;
        public bool IsActive { get; init; }
    }

    private void Reload()
    {
        _settings = _draft.Base;
        _config = _draft.BuildPreviewConfiguration(_store.PayData);
        var activeId = _config.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now)).Id;

        ScheduleList.ItemsSource = _settings.ScheduleProfiles
            .OrderByDescending(s => s.EffectiveFrom)
            .Select(s => new ScheduleRow { Schedule = s, IsActive = s.Id == activeId })
            .ToList();

        if (_selected is not null)
        {
            var resync = ScheduleList.ItemsSource.OfType<ScheduleRow>().FirstOrDefault(r => r.Schedule.Id == _selected.Id);
            if (resync is not null) ScheduleList.SelectedItem = resync;
        }

        if (ScheduleList.SelectedIndex < 0 && ScheduleList.Items.Count > 0) ScheduleList.SelectedIndex = 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScheduleList.SelectedItem is not ScheduleRow row) return;
        _selected = row.Schedule;
        _isNewEntry = false;
        LoadForm(row.Schedule, row.IsActive);
    }

    private void LoadForm(WorkScheduleProfile schedule, bool isActive)
    {
        NameBox.Text = string.IsNullOrWhiteSpace(schedule.Name) ? LocalizationService.Get("Salary.DefaultScheduleName") : schedule.Name;
        StartTime.SelectedTime = schedule.WorkStart;
        EndTime.SelectedTime = schedule.WorkEnd;
        LunchCheck.IsChecked = schedule.LunchBreakEnabled;
        LunchStartTime.SelectedTime = schedule.LunchBreakStart;
        LunchEndTime.SelectedTime = schedule.LunchBreakEnd;
        EffectiveFromBox.Text = schedule.EffectiveFrom.ToString("yyyy-MM-dd");
        DeleteButton.IsEnabled = !isActive;
        ActivateButton.IsEnabled = !isActive;
        CurrentLabel.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        FormError.Text = "";
    }

    private void ClearForm()
    {
        var active = _config.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now));
        _selected = null;
        _isNewEntry = true;
        NameBox.Text = "";
        StartTime.SelectedTime = active.WorkStart;
        EndTime.SelectedTime = active.WorkEnd;
        LunchCheck.IsChecked = active.LunchBreakEnabled;
        LunchStartTime.SelectedTime = active.LunchBreakStart;
        LunchEndTime.SelectedTime = active.LunchBreakEnd;
        EffectiveFromBox.Text = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        DeleteButton.IsEnabled = false;
        ActivateButton.IsEnabled = false;
        CurrentLabel.Visibility = Visibility.Collapsed;
        FormError.Text = "";
        NameBox.Focus();
    }

    private void OnNew(object sender, RoutedEventArgs e) => ClearForm();

    private void OnSaveSchedule(object sender, RoutedEventArgs e)
    {
        FormError.Text = "";
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { FormError.Text = LocalizationService.Get("Error.ScheduleNameRequired"); return; }
        if (!DateOnly.TryParseExact(EffectiveFromBox.Text, "yyyy-MM-dd", out var effectiveFrom)) { FormError.Text = "⚠ 日期格式无效，例如 2026-10-01"; return; }

        var start = StartTime.SelectedTime;
        var end = EndTime.SelectedTime;
        var lunchOn = LunchCheck.IsChecked == true;
        var lunchStart = LunchStartTime.SelectedTime;
        var lunchEnd = LunchEndTime.SelectedTime;

        if (start >= end) { FormError.Text = "⚠ 下班时间必须晚于上班时间"; return; }
        if (lunchOn && (lunchStart >= lunchEnd || lunchStart < start || lunchEnd > end)) { FormError.Text = "⚠ 午休时间必须在工作时间内，且结束时间须晚于开始时间"; return; }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var schedules = new List<WorkScheduleProfile>(_settings.ScheduleProfiles);

        if (_isNewEntry || _selected is null)
        {
            var entry = new WorkScheduleProfile { Name = name, WorkStart = start, WorkEnd = end, LunchBreakEnabled = lunchOn, LunchBreakStart = lunchStart, LunchBreakEnd = lunchEnd, EffectiveFrom = effectiveFrom };
            // Same-day upsert: a new submission replaces the existing version of that date.
            schedules = ProfileVersioning.Upsert(schedules, entry, s => s.EffectiveFrom, (a, b) => a.Id == b.Id);
        }
        else
        {
            var index = schedules.FindIndex(s => s.Id == _selected.Id);
            if (index < 0) return;
            var edited = schedules[index];
            var updated = new WorkScheduleProfile
            {
                Id = edited.EffectiveFrom < today ? Guid.NewGuid().ToString("N") : edited.Id,
                Name = name,
                WorkStart = start,
                WorkEnd = end,
                LunchBreakEnabled = lunchOn,
                LunchBreakStart = lunchStart,
                LunchBreakEnd = lunchEnd,
                EffectiveFrom = effectiveFrom,
            };
            if (edited.EffectiveFrom < today)
            {
                // Never rewrite history: create a new version effective from the requested date.
                schedules = ProfileVersioning.Upsert(schedules, updated, s => s.EffectiveFrom, (a, b) => a.Id == b.Id);
            }
            else
            {
                schedules = ProfileVersioning.Upsert(schedules, updated, s => s.EffectiveFrom, (a, b) => a.Id == b.Id);
            }
        }

        schedules = ProfileVersioning.DeduplicateByDate(schedules, s => s.EffectiveFrom);
        _draft.ScheduleProfiles = schedules;
        _selected = schedules.OrderByDescending(s => s.EffectiveFrom).FirstOrDefault(s => s.Name == name);
        _isNewEntry = false;
        Reload();
    }

    private void OnActivate(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var selected = _settings.ScheduleProfiles.FirstOrDefault(s => s.Id == _selected.Id);
        if (selected is null) return;
        var activated = selected.EffectiveFrom < today ? selected with { EffectiveFrom = today } : selected;
        // "设为当前" owns today's version: other same-date entries are superseded by upsert;
        // historical (< today) entries are never touched.
        var others = _settings.ScheduleProfiles.Where(s => s.Id != selected.Id).ToList();
        var schedules = ProfileVersioning.Upsert(others, activated, s => s.EffectiveFrom, (a, b) => a.Id == b.Id);
        schedules = ProfileVersioning.DeduplicateByDate(schedules, s => s.EffectiveFrom);
        _draft.ScheduleProfiles = schedules;
        _selected = activated;
        _isNewEntry = false;
        Reload();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var activeId = _config.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now)).Id;
        if (_selected.Id == activeId) { FormError.Text = "⚠ 当前使用中的方案不能删除"; return; }
        var schedules = _settings.ScheduleProfiles.Where(s => s.Id != _selected.Id).ToList();
        _draft.ScheduleProfiles = schedules;
        Reload();
        ClearForm();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnLunchChanged(object sender, RoutedEventArgs e) { }
    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); }
}
