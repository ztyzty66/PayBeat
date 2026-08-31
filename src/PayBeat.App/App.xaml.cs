using Microsoft.Win32;
using PayBeat.App.Helpers;
using PayBeat.App.Models;
using PayBeat.App.Services;
using PayBeat.App.ViewModels;
using PayBeat.App.Views;
using System.Windows.Interop;

namespace PayBeat.App;

/// <summary>
/// Application entry point. Owns the top-level object graph: <see cref="ConfigurationStore"/>,
/// <see cref="SettingsService"/>, <see cref="MainViewModel"/>, <see cref="MainWindow"/>,
/// and <see cref="HotkeyService"/>.
/// </summary>
public partial class App
{
    private readonly List<Window> _hiddenWindows = [];
    private HotkeyService? _hotkeyService;
    private MainViewModel? _mainVm;
    private MainWindow? _mainWindow;
    private SettingsService? _settingsService;
    private ConfigurationStore? _store;
    private Mutex? _singleInstanceMutex;
    private SalarySettings? _startupSettings;
    private TrayIconService? _trayIconService;
    private bool _windowsHidden;

    public void ResumeHotkey() => _hotkeyService?.Resume();
    public void SuspendHotkey() => _hotkeyService?.Suspend();

    protected override void OnExit(ExitEventArgs e)
    {
        // Take an idempotent exit snapshot so history is recorded even if the app
        // closes before midnight rollover. Duplicate calls are safe (upsert by date).
        _mainVm?.ExitSnapshot();

        if (_mainWindow != null)
        {
            var pos = _mainWindow.LastKnownPosition
                      ?? new WindowPosition(_mainWindow.Left, _mainWindow.Top, ScreenHelper.GetCurrentScreenDeviceName(_mainWindow));
            SaveWindowPosition(pos);
        }

        SystemEvents.SessionEnding -= OnSessionEnding;
        _trayIconService?.Dispose();
        _hotkeyService?.Dispose();
        _mainVm?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLogger.Initialize();

        // Last-resort logging so a crash is never silent (no dialogs — tray app must not block).
        DispatcherUnhandledException += (_, e) =>
        {
            AppLogger.LogError("DispatcherUnhandledException", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.LogError("AppDomain.UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString()));

        var settings = LoadStartupSettings();

        if (!TryAcquireSingleInstance())
        {
            return;
        }

        SystemEvents.SessionEnding += OnSessionEnding;
        CreateMainViewModelAndWindow();

        if (settings.DisplayMode == DisplayMode.None)
        {
            StartHiddenInTray();
        }
        else
        {
            ShowMainWindow(settings);
        }

        _trayIconService = new TrayIconService(_mainVm!, ActivateMainWindow);

        if (!settings.SetupCompleted)
        {
            var firstRun = new FirstRunWindow(_store!);
            ViewModels.MainViewModel.ApplyTopmostIfNeeded(firstRun);
            firstRun.Show();
        }
    }

    private static void ApplyStartupPlacement(MainWindow mainWindow, SalarySettings settings)
    {
        if (settings.DisplayMode == DisplayMode.Flex)
        {
            var flexBounds = ResolveFlexBounds(mainWindow, settings);
            mainWindow.ApplyFlexBounds(flexBounds);
            return;
        }

        var placement = ResolveSavedPlacement(mainWindow, settings, settings.DisplayMode);
        if (placement == null) return;

        mainWindow.Left = placement.Value.Left;
        mainWindow.Top = placement.Value.Top;
        mainWindow.ClampToWorkArea(placement.Value.Bounds);
    }

    private static WindowPosition? GetSavedPosition(SalarySettings settings, DisplayMode mode) =>
        mode switch
        {
            DisplayMode.Normal => settings.NormalPosition,
            DisplayMode.Mini => settings.MiniPosition,
            _ => null
        };

    private static Rect? ResolveFlexBounds(MainWindow mainWindow, SalarySettings settings)
    {
        if (settings.FlexPosition == null) return null;
        return ScreenHelper.FindScreenBoundsForRestore(0, 0, settings.FlexPosition.ScreenDeviceName, mainWindow);
    }

    private static (double Left, double Top, Rect Bounds)? ResolveSavedPlacement(MainWindow mainWindow, SalarySettings settings, DisplayMode mode)
    {
        var pos = GetSavedPosition(settings, mode);
        if (pos == null) return null;
        var bounds = ScreenHelper.FindScreenBoundsForRestore(pos.Left, pos.Top, pos.ScreenDeviceName, mainWindow);
        return (pos.Left, pos.Top, bounds);
    }

    private void ActivateMainWindow()
    {
        if (_mainWindow == null || _mainVm == null || _mainVm.DisplayMode == DisplayMode.None) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.PlayAttentionAnimation();
    }

    private void CreateMainViewModelAndWindow()
    {
        _mainVm = new MainViewModel(_store!);
        _mainVm.HotkeySettingsChanged += OnHotkeySettingsChanged;

        _mainWindow = new MainWindow { DataContext = _mainVm };
        _mainWindow.SourceInitialized += OnMainWindowSourceInitialized;
        _mainWindow.ContentRendered += OnMainWindowContentRendered;
    }

    private SalarySettings LoadStartupSettings()
    {
        var dataDir = AppDataMigration.ResolveAndMigrate();
        _settingsService = new SettingsService(dataDir);
        var historyService = new HistoryService(Path.Combine(dataDir, "history"));
        _store = new ConfigurationStore(_settingsService, historyService);
        var settings = _store.CurrentSettings;
        _startupSettings = settings;
        LocalizationService.Apply(settings.Language);
        ThemeService.Apply(settings.Theme);
        return settings;
    }

    private void OnHotkeySettingsChanged()
    {
        var s = _store!.CurrentSettings;
        if (_hotkeyService != null)
        {
            var registered = _hotkeyService.Update(s.HotkeyModifiers, s.HotkeyVirtualKey);
            AppLogger.Log($"Hotkey update: {HotkeyService.Format(s.HotkeyModifiers, s.HotkeyVirtualKey)} → {(registered ? "OK" : "FAILED (occupied)")}");
        }
    }

    private void OnMainWindowContentRendered(object? sender, EventArgs e)
    {
        if (_mainWindow == null || _startupSettings == null) return;
        _mainWindow.ContentRendered -= OnMainWindowContentRendered;
        ApplyStartupPlacement(_mainWindow, _startupSettings);
        _mainWindow.IsRestoringStartupPosition = false;
    }

    private void OnMainWindowSourceInitialized(object? sender, EventArgs e)
    {
        var s = _store!.CurrentSettings;
        _hotkeyService = new HotkeyService();
        var registered = _hotkeyService.Register(_mainWindow!, s.HotkeyModifiers, s.HotkeyVirtualKey);
        AppLogger.Log($"Hotkey register: {HotkeyService.Format(s.HotkeyModifiers, s.HotkeyVirtualKey)} → {(registered ? "OK" : "FAILED (occupied)")}");
        _hotkeyService.Triggered += ToggleWindowVisibility;
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        if (_mainWindow == null) return;
        var pos = new WindowPosition(_mainWindow.Left, _mainWindow.Top, ScreenHelper.GetCurrentScreenDeviceName(_mainWindow));
        SaveWindowPosition(pos);
    }

    private void SaveWindowPosition(WindowPosition pos)
    {
        if (_mainVm == null || _store == null) return;
        var settings = _store.CurrentSettings;
        var updated = _mainVm.DisplayMode switch
        {
            DisplayMode.Normal => settings with { NormalPosition = pos },
            DisplayMode.Mini => settings with { MiniPosition = pos },
            DisplayMode.Flex => settings with { FlexPosition = pos },
            _ => settings
        };
        _store.CommitSettingsOnly(updated);
    }

    private void ShowMainWindow(SalarySettings settings)
    {
        _mainWindow!.IsRestoringStartupPosition = settings.DisplayMode is DisplayMode.Normal or DisplayMode.Mini or DisplayMode.Flex;
        if (settings.DisplayMode is DisplayMode.Normal or DisplayMode.Mini)
        {
            var startupPos = GetSavedPosition(settings, settings.DisplayMode);
            if (startupPos != null)
            {
                _mainWindow.Left = startupPos.Left;
                _mainWindow.Top = startupPos.Top;
            }
        }
        _mainWindow.Show();
    }

    private void StartHiddenInTray()
    {
        new WindowInteropHelper(_mainWindow!).EnsureHandle();
        _mainWindow!.ContentRendered -= OnMainWindowContentRendered;
    }

    private void ToggleWindowVisibility()
    {
        if (_windowsHidden)
        {
            foreach (var w in _hiddenWindows) w.Show();
            _hiddenWindows.Clear();
            _windowsHidden = false;
            _mainVm?.ResumeNotifications();
            _trayIconService?.SetHidden(false);
        }
        else
        {
            foreach (Window w in Current.Windows)
            {
                if (w.IsVisible) _hiddenWindows.Add(w);
                w.Hide();
            }
            _windowsHidden = true;
            _mainVm?.SuspendNotifications();
            _trayIconService?.SetHidden(true);
        }
    }

    private bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "PayBeat_SingleInstance", out var createdNew);
        if (createdNew) return true;

        MessageBox.Show(
            (string)FindResource("Error.AlreadyRunning")!,
            "今日薪动",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        Shutdown();
        return false;
    }
}
