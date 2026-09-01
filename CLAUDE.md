# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Prerequisites

- Windows 10/11 x64
- .NET 10 SDK

## Build & Run

```bash
# Build
dotnet build

# Run
dotnet run --project src/PayBeat.App/PayBeat.App.csproj

# Publish (portable, requires .NET 10 Desktop Runtime on target machine)
dotnet publish src/PayBeat.App/PayBeat.App.csproj -c Release

# Publish (self-contained, no runtime prerequisite)
dotnet publish src/PayBeat.App/PayBeat.App.csproj -c Release -r win-x64 --self-contained
```

Output goes to `artifacts/bin/PayBeat.App/release/`.

## Architecture

WPF floating widget app (.NET 10, MVVM). Shows real-time earnings as a borderless, always-on-top window, plus a system tray icon for display-mode switching, Settings/About, and Exit.

**Data flow:**
`DispatcherTimer` (configurable interval) → `MainViewModel.Refresh()` → `SalaryEngine.ComputeDayAt()` → bound properties update the active view template.

**Display modes:** `DisplayMode` has `None`, `Normal`, `Mini`, and `Flex`, swapped inside a single `MainWindow` via `DataTemplate` + `DataTrigger`. Each mode saves its last position independently per screen. `Flex` is a fullscreen "show-off" view with a huge earnings figure and animated background.

**Key entry points:**
- `App.xaml.cs` — owns the object graph (`ConfigurationStore`, `MainViewModel`, `MainWindow`, `HotkeyService`, `TrayIconService`); enforces single-instance via a named `Mutex`; saves window position via `CommitSettingsOnly`; runs `HistoryBackfillService.Backfill()` at startup; takes exit snapshot on exit.
- `MainViewModel.cs` — owns the `DispatcherTimer` and all earnings/display state; `CheckDateRoll()` iterates each missed day individually for multi-day gap recovery; `ScheduleWakeTimer()` computes `min(midnight, next day's WorkStart)` so schedule changes at 00:00 are never missed; `ExitSnapshot()` only finalizes days that are truly completed (time >= WorkEnd, or rest/holiday/PTO).
- `MainWindow.xaml` — borderless `Window` with a `ContentControl` that switches view templates via `DataTrigger` on `DisplayMode`.

**ConfigurationStore (single source of truth):**
- `ConfigurationStore` is the canonical runtime state. ViewModels read from `CurrentSettings`/`CurrentConfiguration`.
- All writes flow through `Commit(settings)` which atomically persists and rebuilds in-memory state.
- `CommitSettingsOnly(settings)` is a lightweight persistence-only update (for window position) that skips hotkey re-registration and full UI rebuilds.
- `CreateDraft()` returns a `ConfigurationDraft` — a deep clone with `SalarySettings.DeepClone()` in the constructor, so draft mutations never affect the store.

**Versioned profiles:**
- `SalaryProfile`, `WorkScheduleProfile`, `WorkWeekPolicy` each carry an `EffectiveFrom` date.
- `ProfileVersioning.Resolve(profiles, date)` returns the latest profile with `EffectiveFrom <= date`.
- `ProfileVersioning.Upsert(profiles, newProfile)` replaces same-date entries (date-keyed, deterministic).
- `ProfileVersioning.DeduplicateByDate(profiles)` ensures at most one entry per date (last-write-wins).
- `ProfileVersioning.Normalize(profiles)` deduplicates and sorts by date (used during migration).

**Schedule history immutability:**
- `ScheduleVersioning` (Domain layer) provides `Activate`, `Edit`, `Delete` as pure functions.
- Historical versions (`EffectiveFrom < today`): never edited or deleted in-place; new versions are created with fresh Ids and `EffectiveFrom = today`.
- Activate on historical versions is allowed ("re-enable from today") — creates a new today-dated version.
- Delete is blocked for historical and active schedules; allowed for future versions.
- Today versions: can be edited in-place.
- Future versions: can be edited or deleted.

**Leave logic:**
- `LeaveRecord.RequestedSpan(schedule)` resolves the wall-clock span for a leave kind.
- With lunch: Morning = `WorkStart → LunchStart`, Afternoon = `LunchEnd → WorkEnd`.
- Without lunch: Morning = first half of work window, Afternoon = second half (split at effective-work-seconds midpoint).
- `LeaveRecord.Validate(schedule)` rejects hourly leave with Start >= End or zero overlap with work hours.

**History and snapshots:**
- `HistoryService` persists per-day and per-month history snapshots to `%APPDATA%\今日薪动\history\`.
- `MainViewModel.CheckDateRoll()` iterates each missed day individually (multi-day gap recovery, month-boundary finalization).
- `MainViewModel.ExitSnapshot()` only finalizes days that are truly completed (time >= WorkEnd, or rest/holiday/PTO). Never writes fake "full day" for days still in progress.
- `HistoryBackfillService.Backfill()` runs at startup to fill any gap between the latest recorded date and yesterday. Idempotent, uses each day's effective configuration.
- `MonthHistory.PassedWorkdaysSnapshot` stores the passed workday count at finalization.
- `DetailWindow` uses `PassedWorkdaysSnapshot`; shows "--" for old files that lack it (never uses `Days.Count`).

**AppData paths:**
- `AppPaths` provides the single source of truth: `DataRoot` = `%APPDATA%\今日薪动`.
- `AppDataMigration` handles resumable, non-destructive migration from legacy `%APPDATA%\PayBeat` using a `.migration-v1-complete` marker. New-data-wins: existing destination settings are never overwritten by legacy.
- `SettingsService.Migrate()` normalizes duplicate EffectiveFrom entries via `ProfileVersioning.Normalize()` on every load.
- All services (`SettingsService`, `HistoryService`, `AppLogger`) resolve paths from `AppPaths`.

**Models:**
- `SalarySettings` — immutable `record`; defaults: `DailySalary=500`, `WorkStart=09:00`, `WorkEnd=18:00`, `Currency="¥"`, `DisplayMode=Normal`, `AlwaysOnTop=false`, `ConfigVersion=3`. Versioned collections: `SalaryProfiles`, `ScheduleProfiles`, `WeekPolicies`, `Overrides`.
- `PayConfiguration` — immutable aggregated view built from `SalarySettings` + `HolidayCalendar`. Provides `ResolveSchedule`, `ResolveSalaryProfile`, `ResolveDayStatus`, `ResolvePlannedStatus`, `PlannedWorkdays`.
- `SalaryEngine` — pure computation engine: `ComputeDay`, `ComputeDayAt`, `ComputeMonth`, `EffectiveLeaveSeconds`.

**UI theme:** Catppuccin-derived palette, with separate `Theme.Light.xaml`/`Theme.Dark.xaml` dictionaries swapped at runtime by `ThemeService`. UI strings via `{DynamicResource}` from `Strings.en.xaml` / `Strings.zh-CN.xaml`.

## Solution Configuration

`PayBeat.slnx` (new XML-based solution format) references the app and test projects.

| File | Purpose |
|------|---------|
| `global.json` | Pins SDK to `10.0.100` with `rollForward: latestMinor` |
| `Directory.Build.props` | Shared build properties: `Nullable`, `ImplicitUsings`, `LangVersion`, `UseArtifactsOutput`, `TreatWarningsAsErrors` |
| `Directory.Packages.props` | Central package versions (`ManagePackageVersionsCentrally=true`) |
| `nuget.config` | Restricts package sources to nuget.org only |

## Tests

```bash
dotnet test tests/PayBeat.Tests/PayBeat.Tests.csproj -c Release
```

Test categories:
- `ScheduleVersioningTests` — activate/edit/delete with historical preservation
- `NoLunchLeaveTests` — morning/afternoon split without lunch, hourly validation
- `DraftIsolationTests` — deep clone isolation, cancel discard, atomic commit
- `MigrationAndNormalizationTests` — ProfileVersioning normalization, history backward compat
- `RolloverAndBackfillTests` — midnight rollover, multi-day gap, clock rollback
- `WorkdayProgressTests` — PlannedWorkdays stability with leave/PTO
- `RealFlowTests` — end-to-end chains (settings → calendar → widget → restart)
- `PersistenceTests` — atomic writes, v1→v3 migration, corrupt recovery
- `ViewModelTests` — ConfigurationStore/ConfigurationDraft propagation

## CI / Release

`.github/workflows/ci.yml` has two jobs:
- `validate` — runs on every push and PR to main; builds and runs all tests.
- `release` — depends on `validate`; runs only on `v*` tag push; publishes portable and self-contained builds, compiles the Inno Setup installer, and creates a GitHub Release.

User settings are persisted to `%APPDATA%\今日薪动\settings.json` (migrated from legacy `%APPDATA%\PayBeat`). The installer/uninstaller preserves both directories — salary history is user data and must not be deleted on uninstall.

## Holiday Coverage

The built-in official holiday dataset (`Resources/holidays.json`) currently covers **2025–2026**. `HolidayCalendar` exposes `CoveredYears`, `CoversYear(year)`, `MinCoveredYear`, and `MaxCoveredYear` for programmatic coverage detection. When a user views an uncovered year (e.g. 2027), the calendar page displays a non-blocking warning: the year falls back to the work week policy and manual overrides. No future official holiday data is ever fabricated.
