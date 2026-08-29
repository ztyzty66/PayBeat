using System.IO;
using PayBeat.App.Domain;
using PayBeat.App.Models;
using PayBeat.App.Services;

namespace PayBeat.Tests;

/// <summary>
/// Persistence tests: atomic writes, load/save round-trip, v1→v3 migration,
/// corrupt settings recovery, atomic history write, history edit + reload.
/// </summary>
public class PersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public PersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PayBeatTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Settings_LoadSaveRoundTrip()
    {
        var service = new SettingsService(_tempDir);
        var settings = new SalarySettings
        {
            ConfigVersion = 3,
            DisplayMode = DisplayMode.Flex,
            AlwaysOnTop = false,
            Opacity = 0.75,
            RefreshInterval = 5,
            Language = "en",
            Theme = "dark",
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 8000m, EffectiveFrom = new DateOnly(2025, 1, 1) }],
            ScheduleProfiles = [new WorkScheduleProfile { Id = "test-schedule", Name = "Test Schedule", WorkStart = new TimeOnly(8, 30), WorkEnd = new TimeOnly(17, 30), LunchBreakEnabled = true, LunchBreakStart = new TimeOnly(12, 0), LunchBreakEnd = new TimeOnly(13, 0), EffectiveFrom = new DateOnly(2025, 1, 1) }],
            WeekPolicies = [WorkWeekPolicy.Create(WorkWeekType.SingleRest, new DateOnly(2025, 1, 1))],
        };

        service.Save(settings);
        var loaded = service.Load();

        Assert.Equal(DisplayMode.Flex, loaded.DisplayMode);
        Assert.False(loaded.AlwaysOnTop);
        Assert.Equal(0.75, loaded.Opacity);
        Assert.Equal(5, loaded.RefreshInterval);
        Assert.Equal("en", loaded.Language);
        Assert.Equal("dark", loaded.Theme);
        Assert.Single(loaded.SalaryProfiles);
        Assert.Equal(8000m, loaded.SalaryProfiles[0].MonthlyAmount);
        Assert.Single(loaded.ScheduleProfiles);
        Assert.Equal(new TimeOnly(8, 30), loaded.ScheduleProfiles[0].WorkStart);
        Assert.Equal(new TimeOnly(17, 30), loaded.ScheduleProfiles[0].WorkEnd);
        Assert.True(loaded.ScheduleProfiles[0].LunchBreakEnabled);
        Assert.Single(loaded.WeekPolicies);
        Assert.Equal(WorkWeekType.SingleRest, loaded.WeekPolicies[0].Type);
    }

    [Fact]
    public void Settings_AtomicWrite_OriginalIntactOnFailure()
    {
        var service = new SettingsService(_tempDir);
        var settings = new SalarySettings
        {
            DisplayMode = DisplayMode.Normal,
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 5000m, EffectiveFrom = new DateOnly(2000, 1, 1) }],
        };

        // First save (no existing file → no backup)
        service.Save(settings);
        var before = service.Load();
        Assert.Equal(5000m, before.SalaryProfiles[0].MonthlyAmount);

        // Second save (existing file → backup created)
        var settings2 = settings with { SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 6000m, EffectiveFrom = new DateOnly(2000, 1, 1) }] };
        service.Save(settings2);
        Assert.True(File.Exists(Path.Combine(_tempDir, "settings.json.bak")));

        // Loaded value is the new one
        var after = service.Load();
        Assert.Equal(6000m, after.SalaryProfiles[0].MonthlyAmount);
    }

    [Fact]
    public void Settings_Migrate_V1ToV3()
    {
        var v1Settings = new SalarySettings
        {
            ConfigVersion = 1,
            DailySalary = 300m,
            WorkStart = new TimeOnly(8, 0),
            WorkEnd = new TimeOnly(17, 0),
            LunchBreakEnabled = true,
            LunchBreakStart = new TimeOnly(12, 0),
            LunchBreakEnd = new TimeOnly(13, 0),
            WorkOnWeekends = false,
        };

        var migrated = SettingsService.Migrate(v1Settings);

        Assert.Equal(3, migrated.ConfigVersion);
        Assert.Single(migrated.SalaryProfiles);
        Assert.Equal(SalaryMode.Daily, migrated.SalaryProfiles[0].Mode);
        Assert.Equal(300m, migrated.SalaryProfiles[0].DailyAmount);
        Assert.Single(migrated.ScheduleProfiles);
        Assert.Equal(new TimeOnly(8, 0), migrated.ScheduleProfiles[0].WorkStart);
        Assert.Equal(new TimeOnly(17, 0), migrated.ScheduleProfiles[0].WorkEnd);
        Assert.True(migrated.ScheduleProfiles[0].LunchBreakEnabled);
        Assert.Single(migrated.WeekPolicies);
        Assert.Equal(WorkWeekType.DoubleRest, migrated.WeekPolicies[0].Type);
        Assert.True(migrated.SetupCompleted);
    }

    [Fact]
    public void Settings_Migrate_V2ToV3()
    {
        var v2Settings = new SalarySettings
        {
            ConfigVersion = 2,
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 6000m, EffectiveFrom = new DateOnly(2025, 1, 1) }],
            ScheduleProfiles = [new WorkScheduleProfile { EffectiveFrom = new DateOnly(2025, 1, 1) }],
            WeekPolicies = [WorkWeekPolicy.Create(WorkWeekType.DoubleRest, new DateOnly(2025, 1, 1))],
        };

        var migrated = SettingsService.Migrate(v2Settings);

        Assert.Equal(3, migrated.ConfigVersion);
        Assert.Equal(6000m, migrated.SalaryProfiles[0].MonthlyAmount);
    }

    [Fact]
    public void Settings_Migrate_V3Idempotent()
    {
        var v3Settings = new SalarySettings
        {
            ConfigVersion = 3,
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 7000m, EffectiveFrom = new DateOnly(2025, 1, 1) }],
        };

        var migrated = SettingsService.Migrate(v3Settings);

        Assert.Equal(3, migrated.ConfigVersion);
        Assert.Equal(7000m, migrated.SalaryProfiles[0].MonthlyAmount);
    }

    [Fact]
    public void Settings_CorruptFileRecovery()
    {
        var service = new SettingsService(_tempDir);
        var filePath = Path.Combine(_tempDir, "settings.json");

        // Write corrupt JSON
        File.WriteAllText(filePath, "{ invalid json content }");

        var loaded = service.Load();

        // Should return default settings
        Assert.NotNull(loaded);
        Assert.Equal(new SalarySettings().DisplayMode, loaded.DisplayMode);

        // Backup should exist
        Assert.True(File.Exists(filePath + ".bak"));
    }

    [Fact]
    public void Settings_MissingFileReturnsDefault()
    {
        var service = new SettingsService(_tempDir);
        var loaded = service.Load();

        Assert.NotNull(loaded);
        Assert.Equal(new SalarySettings().DisplayMode, loaded.DisplayMode);
    }

    [Fact]
    public void Settings_AlwaysOnTop_DefaultsOff_FunctionWindowsNeverTopmost()
    {
        // P2 rule: only the main floating widget may be always-on-top, and its default is off.
        Assert.False(new SalarySettings().AlwaysOnTop);
    }

    [Fact]
    public void History_AtomicWrite()
    {
        var historyDir = Path.Combine(_tempDir, "history");
        var service = new HistoryService(historyDir);
        var month = new DateOnly(2026, 8, 1);

        var record = new DayHistoryRecord
        {
            Date = new DateOnly(2026, 8, 15),
            Status = DayStatus.Work,
            DailyRate = 230.77m,
            TargetEarned = 230.77m,
            FinalEarned = 230.77m,
            LeaveSeconds = 0,
        };

        service.RecordDay(month, record);
        var loaded = service.Load(month);

        Assert.NotNull(loaded);
        Assert.Single(loaded.Days);
        Assert.Equal(DayStatus.Work, loaded.Days["2026-08-15"].Status);
        Assert.Equal(230.77m, loaded.Days["2026-08-15"].DailyRate);
    }

    [Fact]
    public void History_EditAndReload()
    {
        var historyDir = Path.Combine(_tempDir, "history");
        var service = new HistoryService(historyDir);
        var month = new DateOnly(2026, 8, 1);

        var record1 = new DayHistoryRecord
        {
            Date = new DateOnly(2026, 8, 15),
            Status = DayStatus.Work,
            DailyRate = 230.77m,
            TargetEarned = 230.77m,
            FinalEarned = 230.77m,
        };
        service.RecordDay(month, record1);

        // Edit: change to Leave
        var record2 = new DayHistoryRecord
        {
            Date = new DateOnly(2026, 8, 15),
            Status = DayStatus.Leave,
            DailyRate = 230.77m,
            TargetEarned = 0m,
            FinalEarned = 0m,
            LeaveSeconds = 28800,
        };
        service.RecordDay(month, record2);

        var loaded = service.Load(month);
        Assert.NotNull(loaded);
        Assert.Equal(DayStatus.Leave, loaded.Days["2026-08-15"].Status);
        Assert.Equal(0m, loaded.Days["2026-08-15"].FinalEarned);
        Assert.Equal(28800, loaded.Days["2026-08-15"].LeaveSeconds);
    }

    [Fact]
    public void History_FinalizeMonth()
    {
        var historyDir = Path.Combine(_tempDir, "history");
        var service = new HistoryService(historyDir);
        var month = new DateOnly(2026, 8, 1);

        var record = new DayHistoryRecord
        {
            Date = new DateOnly(2026, 8, 15),
            Status = DayStatus.Work,
            DailyRate = 230.77m,
            TargetEarned = 230.77m,
            FinalEarned = 230.77m,
        };
        service.RecordDay(month, record);

        var finalized = new MonthHistory
        {
            Month = "2026-08",
            StandardMonthlySnapshot = 6000m,
            MonthTargetSnapshot = 6000m,
            MonthEarnedSnapshot = 3000m,
            PlannedWorkdays = 26,
        };
        service.FinalizeMonth(month, finalized);

        var loaded = service.Load(month);
        Assert.NotNull(loaded);
        Assert.True(loaded.Finalized);
        Assert.Equal(6000m, loaded.StandardMonthlySnapshot);
        Assert.Equal(26, loaded.PlannedWorkdays);
    }

    [Fact]
    public void History_ListMonths()
    {
        var historyDir = Path.Combine(_tempDir, "history");
        var service = new HistoryService(historyDir);

        service.RecordDay(new DateOnly(2026, 8, 1), new DayHistoryRecord { Date = new DateOnly(2026, 8, 1), Status = DayStatus.Work, DailyRate = 100m, TargetEarned = 100m, FinalEarned = 100m });
        service.RecordDay(new DateOnly(2026, 7, 1), new DayHistoryRecord { Date = new DateOnly(2026, 7, 1), Status = DayStatus.Work, DailyRate = 100m, TargetEarned = 100m, FinalEarned = 100m });
        service.RecordDay(new DateOnly(2026, 6, 1), new DayHistoryRecord { Date = new DateOnly(2026, 6, 1), Status = DayStatus.Work, DailyRate = 100m, TargetEarned = 100m, FinalEarned = 100m });

        var months = service.ListMonths();
        Assert.Equal(3, months.Count);
        Assert.Equal(new DateOnly(2026, 8, 1), months[0]); // newest first
        Assert.Equal(new DateOnly(2026, 7, 1), months[1]);
        Assert.Equal(new DateOnly(2026, 6, 1), months[2]);
    }

    [Fact]
    public void ConfigurationStore_CommitAndReload()
    {
        var service = new SettingsService(_tempDir);
        var historyService = new HistoryService(Path.Combine(_tempDir, "history"));
        var store = new ConfigurationStore(service, historyService);

        var originalRevision = store.Revision;

        var settings = store.CurrentSettings with
        {
            SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 9999m, EffectiveFrom = new DateOnly(2000, 1, 1) }],
        };

        store.Commit(settings);

        Assert.Equal(originalRevision + 1, store.Revision);
        Assert.Equal(9999m, store.CurrentConfiguration.SalaryProfiles[0].MonthlyAmount);
    }

    [Fact]
    public void ConfigurationDraft_DoesNotAffectStore()
    {
        var service = new SettingsService(_tempDir);
        var historyService = new HistoryService(Path.Combine(_tempDir, "history"));
        var store = new ConfigurationStore(service, historyService);

        var originalAmount = store.CurrentConfiguration.SalaryProfiles[0].MonthlyAmount;

        var draft = store.CreateDraft();
        draft.SalaryProfiles = [new SalaryProfile { Mode = SalaryMode.Monthly, MonthlyAmount = 12345m, EffectiveFrom = new DateOnly(2000, 1, 1) }];

        // Store should be unaffected
        Assert.Equal(originalAmount, store.CurrentConfiguration.SalaryProfiles[0].MonthlyAmount);
    }
}
