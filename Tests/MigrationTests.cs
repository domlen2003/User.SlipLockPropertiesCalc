using System;
using System.Collections.Generic;
using System.IO;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.Haptics;
using DivebombLogistics.Haptics.Settings;
using DivebombLogistics.Integration;
using DivebombLogistics.Tests.Fakes;
using Newtonsoft.Json.Linq;

namespace DivebombLogistics.Tests;

/// <summary>
/// Tests of the v3 file layout: <see cref="LegacyMigration"/> (Slip Lock Properties Calc data into
/// <c>PluginsData\DLP</c>), <see cref="HapticsSettingsStore"/>, the file variant of <see cref="AsyncJsonWriter{T}"/>
/// and <see cref="CarFileNaming"/>. Every test works in its own temporary "PluginsData" folder.
/// </summary>
internal sealed class MigrationTests
{
    private static readonly DateTime FixedUtc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- LegacyMigration

    [Test]
    public void Migration_CopiesSettingsAndCarProfilesAndWritesTheMarker()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);
        WriteLegacySettings(paths);
        string lmu = WriteLegacyCar(paths, "LMU", "Ligier JS P325_0a1b2c3d.json", "{\"SchemaVersion\":1,\"CarKey\":\"Ligier JS P325\"}");
        WriteLegacyCar(paths, "IRacing", "Mazda MX-5 Cup_11223344.json", "{\"SchemaVersion\":1,\"CarKey\":\"Mazda MX-5 Cup\"}");
        WriteLegacyCar(paths, "LMU", "Oreca_12345678.json.tmp", "partial");
        WriteLegacyCar(paths, "LMU", "Oreca_12345678.json.bad-20260101-120000", "{broken");
        WriteLegacyCar(paths, "LMU", "Oreca_12345678.unsaved.json", "{}");
        var log = new CountingLog();

        MigrationResult result = LegacyMigration.Run(paths.PluginsData, log, () => FixedUtc);

        Assert.True(result.SettingsMigrated, "settings migrated");
        Assert.Equal(2, result.CarProfiles, "two profiles, side files skipped");
        Assert.True(result.MarkerWritten && !result.Failed, "marker written");
        Assert.Equal(0, log.Errors, "no errors");

        HapticsSettings migrated = JsonFile.Deserialize<HapticsSettings>(File.ReadAllText(paths.NewSettings), out int skipped);
        Assert.Equal(0, skipped, "new file fully readable");
        Assert.Near(50.023490258166667, migrated.TCThrottleBlend, 0, "v1 value kept");
        Assert.Near(5.0967317914924566, migrated.SlipThreshold, 0, "v1 value kept");
        Assert.True(migrated.GateSlipOnThrottle && migrated.GateLockOnBrake, "v1 bools kept");
        Assert.Equal("Mono", migrated.GameCapabilities["LMU"].WheelSpeedMode.ToString(), "capabilities kept");
        Assert.Equal(HapticsSettings.CurrentSchemaVersion, migrated.SchemaVersion, "normalized");

        Assert.True(File.Exists(Path.Combine(paths.NewCars, "LMU", "Ligier JS P325_0a1b2c3d.json")), "LMU profile copied");
        Assert.True(File.Exists(Path.Combine(paths.NewCars, "IRacing", "Mazda MX-5 Cup_11223344.json")), "iRacing profile copied");
        Assert.Equal(File.ReadAllText(lmu), File.ReadAllText(Path.Combine(paths.NewCars, "LMU", "Ligier JS P325_0a1b2c3d.json")), "content unchanged");
        Assert.Equal(2, Directory.GetFiles(paths.NewCars, "*", SearchOption.AllDirectories).Length, "nothing else copied");
        Assert.False(Directory.Exists(paths.NewCars + LegacyMigration.StagingSuffix), "staging folder renamed");

        JObject marker = JsonFile.ParseObject(File.ReadAllText(paths.Marker));
        Assert.Equal(FixedUtc.ToString("o"), (string)marker["FromSlipLockPropertiesCalc"], "time stamp");
        Assert.True((bool)marker["Settings"], "marker settings flag");
        Assert.Equal(2, (int)marker["CarProfiles"], "marker profile count");

        // The old files are the user's backup: untouched.
        Assert.Equal(CoreTests.V1SettingsJson, File.ReadAllText(paths.OldSettings), "old settings untouched");
        Assert.Equal(5, Directory.GetFiles(paths.OldCars, "*", SearchOption.AllDirectories).Length, "old profiles untouched");
    }

    [Test]
    public void Migration_SkipsWhenTheMarkerExists()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);
        WriteLegacySettings(paths);
        WriteLegacyCar(paths, "LMU", "a_00000001.json", "{}");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Marker));
        File.WriteAllText(paths.Marker, "{}");

        MigrationResult result = LegacyMigration.Run(paths.PluginsData, NullLog.Instance);

        Assert.True(result.AlreadyMigrated, "already migrated");
        Assert.False(result.SettingsMigrated || result.CarProfiles > 0 || result.MarkerWritten, "nothing done");
        Assert.False(File.Exists(paths.NewSettings) || Directory.Exists(paths.NewCars), "no new files");
    }

    [Test]
    public void Migration_WithoutOldDataOnlyWritesTheMarker()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);

        MigrationResult result = LegacyMigration.Run(paths.PluginsData, NullLog.Instance, () => FixedUtc);

        Assert.True(result.MarkerWritten, "fresh install: marker written, the check never runs again");
        Assert.False(result.SettingsMigrated, "no settings");
        Assert.Equal(0, result.CarProfiles, "no profiles");
        Assert.False(File.Exists(paths.NewSettings) || Directory.Exists(paths.NewCars), "nothing created");
        Assert.False((bool)JsonFile.ParseObject(File.ReadAllText(paths.Marker))["Settings"], "marker says no settings");
    }

    [Test]
    public void Migration_NeverOverwritesExistingDlpData()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);
        WriteLegacySettings(paths);
        WriteLegacyCar(paths, "LMU", "old_00000001.json", "{\"old\":true}");
        Directory.CreateDirectory(Path.Combine(paths.NewCars, "LMU"));
        File.WriteAllText(Path.Combine(paths.NewCars, "LMU", "new_00000002.json"), "{\"new\":true}");
        File.WriteAllText(paths.NewSettings, "{\"SlipThreshold\":7.0}");

        MigrationResult result = LegacyMigration.Run(paths.PluginsData, NullLog.Instance);

        Assert.False(result.SettingsMigrated, "existing settings kept");
        Assert.Equal(0, result.CarProfiles, "existing Cars folder kept");
        Assert.Equal("{\"SlipThreshold\":7.0}", File.ReadAllText(paths.NewSettings), "settings untouched");
        Assert.False(File.Exists(Path.Combine(paths.NewCars, "LMU", "old_00000001.json")), "no merge into existing cars");
        Assert.True(result.MarkerWritten, "marker written");
    }

    [Test]
    public void Migration_CompletesAPartialEarlierRun()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);
        WriteLegacySettings(paths);
        WriteLegacyCar(paths, "LMU", "a_00000001.json", "{}");
        WriteLegacyCar(paths, "LMU", "b_00000002.json", "{}");

        // An earlier start migrated the settings, then died during the car copy (staging folder left behind).
        Directory.CreateDirectory(Path.GetDirectoryName(paths.NewSettings));
        File.WriteAllText(paths.NewSettings, "{\"SlipThreshold\":9.0}");
        string staging = paths.NewCars + LegacyMigration.StagingSuffix;
        Directory.CreateDirectory(Path.Combine(staging, "LMU"));
        File.WriteAllText(Path.Combine(staging, "LMU", "a_00000001.json"), "{");

        MigrationResult result = LegacyMigration.Run(paths.PluginsData, NullLog.Instance);

        Assert.False(result.SettingsMigrated, "settings were already there");
        Assert.Equal(2, result.CarProfiles, "cars copied completely");
        Assert.Equal("{}", File.ReadAllText(Path.Combine(paths.NewCars, "LMU", "a_00000001.json")), "staging leftover replaced");
        Assert.False(Directory.Exists(staging), "no staging folder left");
        Assert.True(result.MarkerWritten, "marker written");
    }

    [Test]
    public void Migration_CorruptOldSettingsStartWithDefaults()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OldSettings));
        File.WriteAllText(paths.OldSettings, "{\"SlipThreshold\": 5.0, trunc");
        WriteLegacyCar(paths, "LMU", "a_00000001.json", "{}");
        var log = new CountingLog();

        MigrationResult result = LegacyMigration.Run(paths.PluginsData, log);

        Assert.False(result.SettingsMigrated, "corrupt settings not migrated");
        Assert.False(File.Exists(paths.NewSettings), "no new settings file (defaults at load)");
        Assert.Equal(1, result.CarProfiles, "profiles still migrated");
        Assert.True(result.MarkerWritten && !result.Failed, "not retried: the old file stays corrupt");
        Assert.Equal(1, log.Warnings, "warned");
        Assert.Equal("{\"SlipThreshold\": 5.0, trunc", File.ReadAllText(paths.OldSettings), "old file untouched");
    }

    [Test]
    public void Migration_IoFailureLeavesTheMarkerUnwrittenForARetry()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);
        WriteLegacySettings(paths);
        var log = new CountingLog();

        MigrationResult result;
        using (new FileStream(paths.OldSettings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = LegacyMigration.Run(paths.PluginsData, log);
        }

        Assert.True(result.Failed && result.SettingsFailed && !result.CarProfilesFailed, "settings failure reported");
        Assert.False(result.MarkerWritten || File.Exists(paths.Marker), "no marker");
        Assert.True(result.PendingWritten && File.Exists(paths.Pending), "pending record written");
        Assert.Equal(1, log.Errors, "logged");

        MigrationResult retry = LegacyMigration.Run(paths.PluginsData, log);
        Assert.True(retry.Retry && retry.SettingsMigrated && retry.MarkerWritten, "next start migrates");
        Assert.False(File.Exists(paths.Pending), "pending record removed");
    }

    [Test]
    public void Migration_RetryReplacesTheDefaultsADlpSessionWroteAfterAFailure()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);
        WriteLegacySettings(paths);
        WriteLegacyCar(paths, "IRacing", "Old Car_11223344.json", "{\"SchemaVersion\":1,\"CarKey\":\"Old Car\"}");

        MigrationResult first;
        using (new FileStream(paths.OldSettings, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = LegacyMigration.Run(paths.PluginsData, NullLog.Instance);
        }

        Assert.True(first.SettingsFailed && !first.CarProfilesFailed, "only the settings failed");
        Assert.Equal(1, first.CarProfiles, "a settings failure does not skip the car profiles");

        // One DLP session on the defaults: it writes Settings.json (End) and the profile of the driven car.
        var rig = new ModuleTestRig(Path.Combine(paths.PluginsData, "DLP"));
        rig.Add(new HapticsModule());
        rig.SetGame("IRacing", "id", "Mazda MX-5 Cup");
        rig.RunFrames(5, 0.1);
        rig.Host.End(rig.Now);
        string defaults = File.ReadAllText(paths.NewSettings);
        Assert.Near(new HapticsSettings().SlipThreshold, JsonFile.Deserialize<HapticsSettings>(defaults, out _).SlipThreshold, 0, "session wrote defaults");

        MigrationResult second = LegacyMigration.Run(paths.PluginsData, NullLog.Instance, () => FixedUtc);

        Assert.True(second.Retry && second.SettingsMigrated && second.MarkerWritten, "settings migrated over the defaults");
        Assert.Equal(0, second.CarProfiles, "car part was not pending: nothing copied again");
        HapticsSettingsStore.LoadResult loaded = HapticsSettingsStore.Load(Path.Combine(paths.PluginsData, "DLP", "Haptics"), NullLog.Instance);
        Assert.Near(5.0967317914924566, loaded.Settings.SlipThreshold, 0, "the old tuning is active");
        Assert.Equal(defaults, File.ReadAllText(paths.NewSettings + LegacyMigration.PreMigrationSuffix), "replaced file kept");
        Assert.False(File.Exists(paths.Pending), "pending record removed");
        JObject marker = JsonFile.ParseObject(File.ReadAllText(paths.Marker));
        Assert.True((bool)marker["Settings"], "marker settings flag");
        Assert.Equal(1, (int)marker["CarProfiles"], "marker counts the profile of the first run");
    }

    [Test]
    public void Migration_RetryMergesFailedCarProfilesWithoutOverwriting()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);
        WriteLegacyCar(paths, "LMU", "a_00000001.json", "{\"old\":\"a\"}");
        string locked = WriteLegacyCar(paths, "LMU", "b_00000002.json", "{\"old\":\"b\"}");
        WriteLegacyCar(paths, "IRacing", "c_00000003.json", "{\"old\":\"c\"}");
        WriteLegacyCar(paths, "IRacing", "c_00000003.json.tmp", "partial");

        MigrationResult first;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = LegacyMigration.Run(paths.PluginsData, NullLog.Instance);
        }

        Assert.True(first.CarProfilesFailed && !first.SettingsFailed && first.PendingWritten, "car failure recorded");
        Assert.False(Directory.Exists(paths.NewCars) || File.Exists(paths.Marker), "nothing final yet");

        // The session that follows saves the profile of car "a" with newer learned data.
        Directory.CreateDirectory(Path.Combine(paths.NewCars, "LMU"));
        File.WriteAllText(Path.Combine(paths.NewCars, "LMU", "a_00000001.json"), "{\"dlp\":\"a\"}");

        MigrationResult second = LegacyMigration.Run(paths.PluginsData, NullLog.Instance);

        Assert.True(second.Retry && second.MarkerWritten && !second.Failed, "retry completes");
        Assert.Equal(2, second.CarProfiles, "the missing profiles are merged");
        Assert.Equal("{\"dlp\":\"a\"}", File.ReadAllText(Path.Combine(paths.NewCars, "LMU", "a_00000001.json")), "DLP profile kept");
        Assert.Equal("{\"old\":\"b\"}", File.ReadAllText(Path.Combine(paths.NewCars, "LMU", "b_00000002.json")), "b merged");
        Assert.Equal("{\"old\":\"c\"}", File.ReadAllText(Path.Combine(paths.NewCars, "IRacing", "c_00000003.json")), "c merged");
        Assert.Equal(3, Directory.GetFiles(paths.NewCars, "*", SearchOption.AllDirectories).Length, "no temp or side files");
        Assert.False(Directory.Exists(paths.NewCars + LegacyMigration.StagingSuffix), "staging leftover removed");
        Assert.False(File.Exists(paths.Pending), "pending record removed");
        Assert.False((bool)JsonFile.ParseObject(File.ReadAllText(paths.Marker))["Settings"], "no settings to migrate");
    }

    [Test]
    public void Migration_RepeatedFailureKeepsThePendingRecord()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);
        WriteLegacySettings(paths);
        string locked = WriteLegacyCar(paths, "LMU", "a_00000001.json", "{}");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            LegacyMigration.Run(paths.PluginsData, NullLog.Instance);
            MigrationResult again = LegacyMigration.Run(paths.PluginsData, NullLog.Instance);
            Assert.True(again.Retry && again.CarProfilesFailed && again.PendingWritten, "still pending");
            Assert.False(again.SettingsMigrated, "settings not pending: not migrated twice");
        }

        JObject pending = JsonFile.ParseObject(File.ReadAllText(paths.Pending));
        Assert.True((bool)pending["SettingsMigrated"], "earlier settings success remembered");
        Assert.False((bool)pending["SettingsFailed"], "settings not failed");

        MigrationResult last = LegacyMigration.Run(paths.PluginsData, NullLog.Instance);
        Assert.True(last.MarkerWritten && last.CarProfiles == 1, "completed");
        Assert.True((bool)JsonFile.ParseObject(File.ReadAllText(paths.Marker))["Settings"], "marker sums earlier runs");
    }

    [Test]
    public void Migration_UnreadablePendingRecordRetriesEveryPartSafely()
    {
        using var dir = new PersistenceTests.TempDirectory();
        Layout paths = new Layout(dir.Path);
        WriteLegacySettings(paths);
        WriteLegacyCar(paths, "LMU", "a_00000001.json", "{\"old\":true}");
        WriteLegacyCar(paths, "LMU", "b_00000002.json", "{\"old\":true}");
        Directory.CreateDirectory(Path.Combine(paths.NewCars, "LMU"));
        File.WriteAllText(Path.Combine(paths.NewCars, "LMU", "a_00000001.json"), "{\"new\":true}");
        File.WriteAllText(paths.NewSettings, "{\"SlipThreshold\":7.0}");
        File.WriteAllText(paths.Pending, "{ truncated");
        var log = new CountingLog();

        MigrationResult result = LegacyMigration.Run(paths.PluginsData, log);

        Assert.True(result.Retry && result.SettingsMigrated && result.MarkerWritten, "settings retried");
        Assert.Equal(1, result.CarProfiles, "cars merged");
        Assert.Equal("{\"new\":true}", File.ReadAllText(Path.Combine(paths.NewCars, "LMU", "a_00000001.json")), "never overwritten");
        Assert.Equal("{\"SlipThreshold\":7.0}", File.ReadAllText(paths.NewSettings + LegacyMigration.PreMigrationSuffix), "replaced settings kept");
        Assert.Equal(1, log.Warnings, "warned about the record");
    }

    // ---------------------------------------------------------------- HapticsSettingsStore

    [Test]
    public void SettingsStore_MissingCorruptLoadedAndLocked()
    {
        using var dir = new PersistenceTests.TempDirectory();
        string path = HapticsSettingsStore.GetPath(dir.Path);
        var log = new CountingLog();

        HapticsSettingsStore.LoadResult missing = HapticsSettingsStore.Load(dir.Path, log);
        Assert.Equal(JsonReadStatus.Missing, missing.Status, "missing");
        Assert.Equal(new HapticsSettings().SlipThreshold, missing.Settings.SlipThreshold, "defaults");
        Assert.Equal(path, missing.SavePath, "saves to Settings.json");

        File.WriteAllText(path, JsonFile.Serialize(new HapticsSettings { SlipThreshold = 12.5, ShowDebugView = true }));
        HapticsSettingsStore.LoadResult loaded = HapticsSettingsStore.Load(dir.Path, log);
        Assert.Equal(JsonReadStatus.Loaded, loaded.Status, "loaded");
        Assert.Equal(12.5, loaded.Settings.SlipThreshold, "value");
        Assert.True(loaded.Settings.ShowDebugView, "bool");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            HapticsSettingsStore.LoadResult locked = HapticsSettingsStore.Load(dir.Path, log);
            Assert.Equal(JsonReadStatus.IoError, locked.Status, "locked");
            Assert.Equal(Path.Combine(dir.Path, HapticsSettingsStore.UnsavedFileName), locked.SavePath, "saves redirected");
            Assert.Equal(1, log.Errors, "logged");
        }

        File.WriteAllText(path, "{ not json");
        HapticsSettingsStore.LoadResult corrupt = HapticsSettingsStore.Load(dir.Path, log, () => new DateTime(2026, 1, 2, 3, 4, 5));
        Assert.Equal(JsonReadStatus.Corrupt, corrupt.Status, "corrupt");
        Assert.False(File.Exists(path), "moved aside");
        Assert.True(File.Exists(path + JsonFile.QuarantineInfix + "20260102-030405"), "quarantined");
        Assert.Equal(path, corrupt.SavePath, "the next save writes a fresh file");
    }

    // ---------------------------------------------------------------- AsyncJsonWriter (file) / CarFileNaming

    [Test]
    public void AsyncJsonWriter_FileVariantWritesLoadableJsonAtomically()
    {
        using var dir = new PersistenceTests.TempDirectory();
        string path = Path.Combine(dir.Path, "Haptics", HapticsSettingsStore.FileName);
        var writer = new AsyncJsonWriter<HapticsSettings>(path, NullLog.Instance);
        Assert.Equal(path, writer.FilePath);

        var live = new HapticsSettings { SlipThreshold = 3.25 };
        live.GetCalibration("LMU").SteeringSignVerified = true;
        writer.SaveAsync(live);
        Assert.True(writer.WaitForPendingWrites(TimeSpan.FromSeconds(5)), "written");
        Assert.True(writer.Save(live), "synchronous save");

        HapticsSettingsStore.LoadResult back = HapticsSettingsStore.Load(Path.GetDirectoryName(path), NullLog.Instance);
        Assert.Equal(JsonReadStatus.Loaded, back.Status, "loads");
        Assert.Equal(3.25, back.Settings.SlipThreshold, "value");
        Assert.True(back.Settings.GetCalibration("lmu").SteeringSignVerified, "nested value");
        Assert.False(File.Exists(path + JsonFile.TempSuffix), "no temp file");
    }

    [Test]
    public void CarFileNaming_MatchesTheHapticsProfilePaths()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);
        foreach (string key in new[] { "Ligier JS P325", "GT3_Iron Lynx 2026_61", "CON", "a/b:c", string.Empty })
        {
            string expected = Path.Combine(dir.Path, CarFileNaming.CarsFolderName, "LMU", CarFileNaming.BuildCarFileStem(key) + CarFileNaming.FileExtension);
            Assert.Equal(expected, store.GetProfilePath("LMU", key), "path of " + key);
            Assert.Equal(expected, CarFileNaming.GetCarFilePath(dir.Path, "LMU", key), "shared naming of " + key);
        }
    }

    private static void WriteLegacySettings(Layout paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OldSettings));
        File.WriteAllText(paths.OldSettings, CoreTests.V1SettingsJson);
    }

    private static string WriteLegacyCar(Layout paths, string sim, string fileName, string content)
    {
        string folder = Path.Combine(paths.OldCars, sim);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, fileName);
        File.WriteAllText(file, content);
        return file;
    }

    /// <summary>Old and new paths below a temporary "PluginsData" folder.</summary>
    private sealed class Layout
    {
        public Layout(string root)
        {
            PluginsData = Path.Combine(root, "PluginsData");
            OldSettings = Path.Combine(PluginsData, LegacyMigration.CommonFolderName, LegacyMigration.LegacySettingsFileName);
            OldCars = Path.Combine(PluginsData, LegacyMigration.LegacyDataFolderName, "Cars");
            NewSettings = Path.Combine(PluginsData, "DLP", "Haptics", "Settings.json");
            NewCars = Path.Combine(PluginsData, "DLP", "Haptics", "Cars");
            Marker = Path.Combine(PluginsData, "DLP", LegacyMigration.MarkerFileName);
            Pending = Path.Combine(PluginsData, "DLP", LegacyMigration.PendingFileName);
        }

        public string PluginsData { get; }

        public string OldSettings { get; }

        public string OldCars { get; }

        public string NewSettings { get; }

        public string NewCars { get; }

        public string Marker { get; }

        public string Pending { get; }
    }

    /// <summary>ILog counting messages per level.</summary>
    private sealed class CountingLog : ILog
    {
        public int Infos { get; private set; }

        public int Warnings { get; private set; }

        public int Errors { get; private set; }

        public List<string> Lines { get; } = new List<string>();

        public void Info(string message)
        {
            Infos++;
            Lines.Add(message);
        }

        public void Warn(string message)
        {
            Warnings++;
            Lines.Add(message);
        }

        public void Error(string message)
        {
            Errors++;
            Lines.Add(message);
        }
    }
}
