using PayBeat.App.Domain;
using PayBeat.App.Models;

namespace PayBeat.App.Services;

/// <summary>
/// Single runtime source of truth for all application configuration. ViewModels read from
/// <see cref="Current"/> instead of independently loading from disk. Persisted changes flow
/// through <see cref="Commit"/>, which atomically writes to disk and then rebuilds the
/// in-memory configuration. All subscribers are notified via <see cref="ConfigurationChanged"/>.
/// </summary>
public sealed class ConfigurationStore
{
    private readonly SettingsService _settingsService;
    private readonly PayDataService _payData;
    private SalarySettings _currentSettings;
    private PayConfiguration _currentConfig;
    private long _revision;

    public ConfigurationStore(SettingsService settingsService, HistoryService historyService)
    {
        _settingsService = settingsService;
        _payData = new PayDataService(settingsService, historyService);
        _currentSettings = settingsService.Load();
        _currentConfig = _payData.BuildConfiguration(_currentSettings);
    }

    /// <summary>Raised after a successful <see cref="Commit"/> or <see cref="Reload"/>.</summary>
    public event Action? ConfigurationChanged;

    /// <summary>Raised when the hotkey settings change so App can re-register the global hotkey.</summary>
    public event Action? HotkeySettingsChanged;

    /// <summary>Current persisted settings (never stale — only updated by Commit/Reload).</summary>
    public SalarySettings CurrentSettings => _currentSettings;

    /// <summary>Current immutable configuration built from <see cref="CurrentSettings"/>.</summary>
    public PayConfiguration CurrentConfiguration => _currentConfig;

    /// <summary>Monotonically increasing version counter; bumped on every Commit/Reload.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>The underlying settings service (for persistence operations only).</summary>
    public SettingsService SettingsService => _settingsService;

    /// <summary>The underlying pay data service (for history snapshots).</summary>
    public PayDataService PayData => _payData;

    /// <summary>
    /// Validates, normalizes, and atomically persists a new settings snapshot.
    /// Rebuilds the in-memory configuration and notifies all subscribers.
    /// </summary>
    public void Commit(SalarySettings settings)
    {
        _settingsService.Save(settings);
        _currentSettings = settings;
        _currentConfig = _payData.BuildConfiguration(settings);
        Interlocked.Increment(ref _revision);
        ConfigurationChanged?.Invoke();
        HotkeySettingsChanged?.Invoke();
    }

    /// <summary>
    /// Re-reads settings from disk and rebuilds the in-memory configuration.
    /// Used at startup and for explicit reload scenarios.
    /// </summary>
    public void Reload()
    {
        _currentSettings = _settingsService.Load();
        _currentConfig = _payData.BuildConfiguration(_currentSettings);
        Interlocked.Increment(ref _revision);
        ConfigurationChanged?.Invoke();
    }

    /// <summary>
    /// Creates a <see cref="ConfigurationDraft"/> initialized from the current settings.
    /// The draft is a mutable snapshot used by SettingsWindow and its child editors.
    /// </summary>
    public ConfigurationDraft CreateDraft() => new(_currentSettings);
}
