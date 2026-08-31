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

    // In-window new-schedule draft: appears as a second list card immediately ("unsaved"),
    // joins the ConfigurationDraft when the user presses the row's save ("pending"), and is
    // only persisted to the store by the main settings save.
    private WorkScheduleProfile? _pendingNew;
    private readonly HashSet<string> _pendingSavedIds = new(StringComparer.Ordinal);

    public ScheduleManagerWindow(ConfigurationStore store, ConfigurationDraft draft, MainViewModel mainVm)
    {
        InitializeComponent();
        _store = store;
        _draft = draft;
        _mainVm = mainVm;
        Owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        Reload();
    }

    // Row presentation lives in PayBeat.App.ViewModels.ScheduleRowVm so the list binds to a
    // real template payload instead of Object.ToString().

    private void Reload()
    {
        _settings = _draft.Base;
        _config = _draft.BuildPreviewConfiguration(_store.PayData);
        var activeId = _config.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now)).Id;

        ScheduleList.ItemsSource = ScheduleListPresenter.BuildRows(
            _settings.ScheduleProfiles.OrderByDescending(s => s.EffectiveFrom).ToList(),
            activeId,
            _pendingNew,
            _pendingSavedIds);

        if (_selected is not null)
        {
            var resync = ScheduleList.ItemsSource.OfType<ScheduleRowVm>().FirstOrDefault(r => r.Schedule.Id == _selected.Id);
            if (resync is not null) ScheduleList.SelectedItem = resync;
        }
        else if (ScheduleList.SelectedIndex < 0 && ScheduleList.Items.Count > 0)
        {
            ScheduleList.SelectedIndex = 0;
        }

        if (ScheduleList.SelectedIndex < 0 && ScheduleList.Items.Count > 0) ScheduleList.SelectedIndex = 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScheduleList.SelectedItem is not ScheduleRowVm row) return;
        _selected = row.Schedule;
        _isNewEntry = row.Schedule.Id == _pendingNew?.Id;
        LoadForm(row.Schedule, row.IsActive, isPending: _isNewEntry);
    }

    private void LoadForm(WorkScheduleProfile schedule, bool isActive, bool isPending = false)
    {
        NameBox.Text = string.IsNullOrWhiteSpace(schedule.Name) ? LocalizationService.Get("Salary.DefaultScheduleName") : schedule.Name;
        StartTime.SelectedTime = schedule.WorkStart;
        EndTime.SelectedTime = schedule.WorkEnd;
        LunchCheck.IsChecked = schedule.LunchBreakEnabled;
        LunchStartTime.SelectedTime = schedule.LunchBreakStart;
        LunchEndTime.SelectedTime = schedule.LunchBreakEnd;
        EffectiveFromBox.Text = schedule.EffectiveFrom.ToString("yyyy-MM-dd");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var isHistorical = schedule.EffectiveFrom < today;
        // An unsaved in-window draft or historical version cannot be activated or deleted.
        DeleteButton.IsEnabled = !isActive && !isPending && !isHistorical;
        ActivateButton.IsEnabled = !isActive && !isPending && !isHistorical;
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

    private void OnNew(object sender, RoutedEventArgs e)
    {
        // Create the pending card FIRST so the user sees a second row appear immediately,
        // then select it so the form edits the new schedule — not the active one.
        var active = _config.ResolveSchedule(DateOnly.FromDateTime(DateTime.Now));
        _pendingNew = ScheduleListPresenter.CreatePending(active);
        _selected = _pendingNew;
        _isNewEntry = true;
        Reload();
        LoadForm(_pendingNew, isActive: false, isPending: true);
        NameBox.Focus();
    }

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
            var entry = new WorkScheduleProfile
            {
                Id = _selected?.Id ?? Guid.NewGuid().ToString("N"),
                Name = name,
                WorkStart = start,
                WorkEnd = end,
                LunchBreakEnabled = lunchOn,
                LunchBreakStart = lunchStart,
                LunchBreakEnd = lunchEnd,
                EffectiveFrom = effectiveFrom,
            };
            schedules = ProfileVersioning.Upsert(schedules, entry, s => s.EffectiveFrom, (a, b) => a.Id == b.Id);
        }
        else
        {
            var existing = schedules.FirstOrDefault(s => s.Id == _selected.Id);
            if (existing is null) return;
            var edited = new WorkScheduleProfile
            {
                Id = existing.Id,
                Name = name,
                WorkStart = start,
                WorkEnd = end,
                LunchBreakEnabled = lunchOn,
                LunchBreakStart = lunchStart,
                LunchBreakEnd = lunchEnd,
                EffectiveFrom = effectiveFrom,
            };
            schedules = ScheduleVersioning.Edit(schedules, edited, today);
        }

        _draft.ScheduleProfiles = schedules;
        if (_selected is null)
        {
            Reload();
            return;
        }
        var savedId = _selected.Id;
        if (_pendingNew is not null && savedId == _pendingNew.Id)
        {
            // The pending card graduated into the draft: it now shows "pending" until the
            // main settings save commits it.
            _pendingSavedIds.Add(savedId);
            _pendingNew = null;
        }

        _selected = schedules.FirstOrDefault(s => s.Id == savedId) ?? schedules.OrderByDescending(s => s.EffectiveFrom).First();
        _isNewEntry = false;
        Reload();
    }

    private void OnActivate(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var result = ScheduleVersioning.Activate(
            _settings.ScheduleProfiles, _selected.Id, today);
        if (result is null) return;
        _draft.ScheduleProfiles = result;
        _selected = result.FirstOrDefault(s => s.Id == _selected.Id)
                    ?? result.OrderByDescending(s => s.EffectiveFrom).First();
        _isNewEntry = false;
        Reload();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var (success, schedules) = ScheduleVersioning.Delete(
            _settings.ScheduleProfiles, _selected.Id, today, _config);
        if (!success)
        {
            var activeId = _config.ResolveSchedule(today).Id;
            if (_selected.Id == activeId)
                FormError.Text = "⚠ 当前使用中的方案不能删除";
            else if (_selected.EffectiveFrom < today)
                FormError.Text = "⚠ 历史版本不能删除";
            return;
        }
        _draft.ScheduleProfiles = schedules;
        _pendingSavedIds.Remove(_selected.Id);
        _selected = null;
        Reload();
        ClearForm();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnLunchChanged(object sender, RoutedEventArgs e) { }
    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); }
}
