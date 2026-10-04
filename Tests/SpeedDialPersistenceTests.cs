using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.SpeedDial;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.SpeedDial.Persistence;

namespace DivebombLogistics.Tests;

/// <summary>
/// <see cref="SpeedDialStore"/> (and its <see cref="OrderedFileWriter"/>): settings and per-car files under the module
/// folder, the robustness rules (missing, corrupt, unreadable, failed writes), export/import and the shared car file
/// naming.
/// </summary>
internal static class SpeedDialPersistenceTests
{
    private const string Sim = "LMU";
    private const string Car = "Ligier JS P325";
    private const string QuarantineTimestampFormat = "yyyyMMdd-HHmmss";

    private static readonly DateTime FixedUtc = new DateTime(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    // ---------------------------------------------------------------- settings

    [Test]
    public static void Settings_MissingFileGivesNormalizedDefaults()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new SpeedDialStore(dir.Path, NullLog.Instance);

        SpeedDialSettings settings = store.LoadSettings(out JsonReadStatus status);

        Assert.Equal(JsonReadStatus.Missing, status);
        Assert.Equal(Path.Combine(dir.Path, "Settings.json"), store.SettingsPath, "settings path");
        Assert.Equal(store.SettingsPath, store.SettingsSavePath, "saves go to the settings file");
        Assert.Equal(SpeedDialSettings.DefaultSlotCount, settings.SlotCount);
        Assert.Equal(DialChannels.Count, settings.Channels.Count, "one binding per channel");
        Assert.Equal("TractionControl+", settings.GetBinding(DialChannel.Tc1).IncreaseRole);
        Assert.Equal("BrakeBalanceRear", settings.GetBinding(DialChannel.BrakeBias).DecreaseRole);
        Assert.Equal(2, settings.Pairs.Count, "default pairs");
        Assert.Equal(DialTiming.DefaultPressMs, settings.Timing.PressMs);
        Assert.False(File.Exists(store.SettingsPath), "loading writes nothing");
    }

    [Test]
    public static void Settings_SaveAndLoadRoundTrip()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new SpeedDialStore(dir.Path, NullLog.Instance);
        SpeedDialSettings settings = store.LoadSettings(out _);
        settings.SlotCount = 7;
        settings.GetBinding(DialChannel.Abs).IncreaseRole = "MyAbs+";
        settings.GetBinding(DialChannel.Tc3).Enabled = false;
        settings.Pairs.Add(SetResetPairDefinition.Create("Wet", DialChannel.Tc1, DialChannel.Abs));
        settings.Timing.GapMs = 120;
        settings.ChannelOrder = new[] { "BB", "TC1" };
        settings.Normalize();

        Assert.True(store.SaveSettings(settings), "synchronous save");
        SpeedDialSettings loaded = new SpeedDialStore(dir.Path, NullLog.Instance).LoadSettings(out JsonReadStatus status);

        Assert.Equal(JsonReadStatus.Loaded, status);
        Assert.Equal(7, loaded.SlotCount);
        Assert.Equal("MyAbs+", loaded.GetBinding(DialChannel.Abs).IncreaseRole);
        Assert.False(loaded.GetBinding(DialChannel.Tc3).Enabled, "disabled binding");
        Assert.Equal("Wet", loaded.Pairs[2].Name);
        Assert.Equal("BB,TC1,TC2,TC3,ABS", string.Join(",", loaded.ChannelOrder));
        Assert.Equal(JsonFile.Serialize(settings), JsonFile.Serialize(loaded), "whole object");
    }

    [Test]
    public static void Settings_AsyncSavesAreWrittenAndTheNewestWins()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new SpeedDialStore(dir.Path, NullLog.Instance);
        SpeedDialSettings settings = store.LoadSettings(out _);

        for (int slots = SpeedDialSettings.MinSlotCount; slots <= SpeedDialSettings.MaxSlotCount; slots++)
        {
            settings.SlotCount = slots;
            store.SaveSettingsAsync(settings); // copied now: later edits of the live object do not leak into this save
        }

        settings.SlotCount = 3; // not saved
        Assert.True(store.WaitForPendingWrites(WriteTimeout), "writes finished");
        Assert.Equal(SpeedDialSettings.MaxSlotCount, new SpeedDialStore(dir.Path, NullLog.Instance).LoadSettings(out _).SlotCount, "newest copy written");
    }

    [Test]
    public static void Settings_LoadNormalizesHandEditedValues()
    {
        using var dir = new PersistenceTests.TempDirectory();
        File.WriteAllText(
            Path.Combine(dir.Path, SpeedDialStore.SettingsFileName),
            "{\"SlotCount\":42,\"Timing\":{\"PressMs\":1},\"Channels\":{\"tc1\":{\"Enabled\":false,\"IncreaseRole\":\" Up \"}},\"Pairs\":[]}");
        var store = new SpeedDialStore(dir.Path, NullLog.Instance);

        SpeedDialSettings settings = store.LoadSettings(out JsonReadStatus status);

        Assert.Equal(JsonReadStatus.Loaded, status);
        Assert.Equal(SpeedDialSettings.MaxSlotCount, settings.SlotCount, "slot count clamped");
        Assert.Equal(DialTiming.MinPressMs, settings.Timing.PressMs, "press clamped");
        Assert.Equal(DialChannels.Count, settings.Channels.Count, "missing bindings added");
        Assert.False(settings.GetBinding(DialChannel.Tc1).Enabled, "edited binding kept");
        Assert.Equal("Up", settings.GetBinding(DialChannel.Tc1).IncreaseRole, "trimmed");
        Assert.Equal(2, settings.Pairs.Count, "no pairs: defaults");
    }

    [Test]
    public static void Settings_CorruptFileIsQuarantined()
    {
        using var dir = new PersistenceTests.TempDirectory();
        string path = Path.Combine(dir.Path, SpeedDialStore.SettingsFileName);
        File.WriteAllText(path, "{ \"SlotCount\": ");
        var log = new RecordingLog();
        var store = new SpeedDialStore(dir.Path, log, () => FixedUtc, null);

        SpeedDialSettings settings = store.LoadSettings(out JsonReadStatus status);

        Assert.Equal(JsonReadStatus.Corrupt, status);
        Assert.Equal(SpeedDialSettings.DefaultSlotCount, settings.SlotCount, "defaults");
        string quarantined = path + JsonFile.QuarantineInfix + FixedUtc.ToLocalTime().ToString(QuarantineTimestampFormat, CultureInfo.InvariantCulture);
        Assert.True(File.Exists(quarantined), "moved aside to " + quarantined);
        Assert.False(File.Exists(path), "no longer loaded");
        Assert.Equal(1, log.Warnings.Count, "reported");
        Assert.Equal(path, store.SettingsSavePath, "saves go to the real file again");
        Assert.True(store.SaveSettings(settings) && File.Exists(path), "next save recreates it");
    }

    [Test]
    public static void Settings_UnreadableFileIsNeverOverwritten()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var first = new SpeedDialStore(dir.Path, NullLog.Instance);
        SpeedDialSettings original = first.LoadSettings(out _);
        original.SlotCount = 6;
        Assert.True(first.SaveSettings(original), "initial file");
        string path = first.SettingsPath;
        string before = File.ReadAllText(path);

        var log = new RecordingLog();
        var store = new SpeedDialStore(dir.Path, log);
        SpeedDialSettings settings;
        JsonReadStatus status;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = store.LoadSettings(out status);
        }

        Assert.Equal(JsonReadStatus.IoError, status);
        Assert.Equal(SpeedDialSettings.DefaultSlotCount, settings.SlotCount, "defaults for the session");
        Assert.Equal(Path.Combine(dir.Path, SpeedDialStore.UnsavedSettingsFileName), store.SettingsSavePath, "side file");
        Assert.Equal(1, log.Errors.Count, "reported");

        settings.SlotCount = 5;
        store.SaveSettingsAsync(settings);
        Assert.True(store.WaitForPendingWrites(WriteTimeout), "async save finished");
        Assert.True(store.SaveSettings(settings), "End save");
        Assert.Equal(before, File.ReadAllText(path), "unread file untouched");
        Assert.Equal(5, JsonFile.Deserialize<SpeedDialSettings>(File.ReadAllText(store.SettingsSavePath), out _).SlotCount, "session saved to the side file");
    }

    [Test]
    public static void Settings_SecondLockedSessionContinuesFromTheSideFile()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var first = new SpeedDialStore(dir.Path, NullLog.Instance);
        Assert.True(first.SaveSettings(first.LoadSettings(out _)), "initial file");
        string path = first.SettingsPath;
        string side = Path.Combine(dir.Path, SpeedDialStore.UnsavedSettingsFileName);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Session 1 saves its edit to the side file.
            var session1 = new SpeedDialStore(dir.Path, NullLog.Instance, () => FixedUtc, null);
            SpeedDialSettings s1 = session1.LoadSettings(out _);
            s1.SlotCount = 5;
            Assert.True(session1.SaveSettings(s1), "session 1 saved");

            // Session 2 (file still locked) continues from the side file instead of overwriting it with defaults.
            var log = new RecordingLog();
            var session2 = new SpeedDialStore(dir.Path, log, () => FixedUtc, null);
            SpeedDialSettings s2 = session2.LoadSettings(out JsonReadStatus status);
            Assert.Equal(JsonReadStatus.IoError, status);
            Assert.Equal(5, s2.SlotCount, "session 1's edit kept");
            Assert.Equal(side, session2.SettingsSavePath, "keeps saving to the side file");

            // Session 3: the side file is locked too, so it saves to a new, timestamped side file.
            using (new FileStream(side, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var session3 = new SpeedDialStore(dir.Path, NullLog.Instance, () => FixedUtc, null);
                SpeedDialSettings s3 = session3.LoadSettings(out _);
                Assert.Equal(SpeedDialSettings.DefaultSlotCount, s3.SlotCount, "defaults");
                Assert.True(session3.SettingsSavePath != side && session3.SettingsSavePath.Contains(".unsaved-"), "timestamped: " + session3.SettingsSavePath);
                Assert.True(session3.SaveSettings(s3), "session 3 saved");
            }

            Assert.Equal(5, JsonFile.Deserialize<SpeedDialSettings>(File.ReadAllText(side), out _).SlotCount, "side file never overwritten by defaults");
        }
    }

    // ---------------------------------------------------------------- car data

    [Test]
    public static void CarData_MissingFileGivesNewNormalizedData()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new SpeedDialStore(dir.Path, NullLog.Instance);

        SpeedDialCarData data = store.LoadCarData(Sim, Car, "Ligier (display)");

        Assert.Equal(Sim, data.SimKey);
        Assert.Equal(Car, data.CarKey);
        Assert.Equal("Ligier (display)", data.DisplayName);
        Assert.Equal(0, data.Presets.Count, "no presets");
        Assert.Equal(SpeedDialSettings.MaxSlotCount, data.SlotPresetIds.Length, "slots");
        Assert.Equal(SpeedDialSettings.MaxPairCount, data.PairSnapshots.Count, "pair snapshots");
        Assert.Equal(DialChannels.Count, data.Learning.Count, "learning entries");
        Assert.False(store.CarDataExists(Sim, Car), "nothing stored");
        Assert.False(File.Exists(store.GetCarFilePath(Sim, Car)), "loading writes nothing");
        Assert.Equal(Car, store.LoadCarData(Sim, Car, null).DisplayName, "display name falls back to the key");
    }

    [Test]
    public static void CarData_RoundTripsEveryField()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new SpeedDialStore(dir.Path, NullLog.Instance, () => FixedUtc, null);
        SpeedDialCarData data = store.LoadCarData(Sim, Car, Car);
        DialPreset quali = DialPreset.Create("Quali");
        quali.SetValue(DialChannel.Tc1, 3);
        quali.SetValue(DialChannel.BrakeBias, 54.2);
        DialPreset race = DialPreset.Create("Race");
        race.SetValue(DialChannel.Abs, 5);
        data.Presets.Add(quali);
        data.Presets.Add(race);
        data.SlotPresetIds[0] = quali.Id;
        data.SlotPresetIds[3] = race.Id;
        data.SelectedPresetId = race.Id;
        data.GetPairSnapshot(1).SetValue(DialChannel.BrakeBias, 55.5);
        data.GetPairSnapshot(1).CapturedUtc = FixedUtc;
        ChannelLearning learning = data.GetLearning(DialChannel.Tc2);
        learning.Direction = ChannelLearning.IncreaseLowers;
        learning.DirectionConfirmed = true;
        learning.RecordStep(1);
        learning.Observe(2);
        learning.Observe(7);

        Assert.True(store.SaveCarData(data), "save");
        Assert.True(store.CarDataExists(Sim, Car), "stored");
        SpeedDialCarData loaded = new SpeedDialStore(dir.Path, NullLog.Instance).LoadCarData(Sim, Car, string.Empty);

        Assert.Equal(FixedUtc, loaded.LastUpdatedUtc, "save stamp");
        Assert.Equal("Race", loaded.GetSlotPreset(3).Name, "slot");
        Assert.Equal(race.Id, loaded.SelectedPresetId, "selection");
        Assert.True(loaded.Presets[0].TryGetValue(DialChannel.BrakeBias, out double bb) && bb == 54.2, "preset value");
        Assert.True(loaded.GetPairSnapshot(1).TryGetValue(DialChannel.BrakeBias, out double stored) && stored == 55.5, "pair value");
        Assert.Equal<DateTime?>(FixedUtc, loaded.GetPairSnapshot(1).CapturedUtc, "capture time");
        ChannelLearning loadedLearning = loaded.GetLearning(DialChannel.Tc2);
        Assert.Equal(ChannelLearning.IncreaseLowers, loadedLearning.Direction);
        Assert.True(loadedLearning.DirectionConfirmed, "confirmed");
        Assert.Equal(1.0, loadedLearning.Step);
        Assert.Equal(2.0, loadedLearning.ObservedMin);
        Assert.Equal(7.0, loadedLearning.ObservedMax);
        Assert.True(double.IsNaN(loaded.GetLearning(DialChannel.Tc1).ObservedMin), "unknown range stays NaN");
        Assert.Equal(JsonFile.Serialize(data), JsonFile.Serialize(loaded), "whole object");
    }

    [Test]
    public static void CarData_FileNamingIsSharedWithHaptics()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new SpeedDialStore(dir.Path, NullLog.Instance);

        string path = store.GetCarFilePath(Sim, "Ferrari 296 GT3");

        Assert.Equal(CarFileNaming.GetCarFilePath(dir.Path, Sim, "Ferrari 296 GT3"), path, "CarFileNaming");
        Assert.Equal(Path.Combine(dir.Path, "Cars", Sim, CarFileNaming.BuildCarFileStem("Ferrari 296 GT3") + ".json"), path, "Cars\\<Sim>\\<stem>.json");
        string stem = Path.GetFileNameWithoutExtension(path);
        Assert.True(stem.StartsWith("Ferrari 296 GT3_", StringComparison.Ordinal), "readable key: " + stem);
        Assert.Equal(8, stem.Length - stem.LastIndexOf('_') - 1, "fnv1a8 suffix");
        Assert.False(string.Equals(store.GetCarFilePath(Sim, "a/b"), store.GetCarFilePath(Sim, "a_b"), StringComparison.OrdinalIgnoreCase), "sanitized keys stay distinct");
        Assert.False(string.Equals(store.GetCarFilePath(Sim, "Car"), store.GetCarFilePath(Sim, "CAR"), StringComparison.OrdinalIgnoreCase), "case-only differences stay distinct");
        Assert.Equal(
            Path.Combine(dir.Path, "Cars", Sim, "x_0000abcd.unsaved.json"),
            SpeedDialStore.UnsavedPath(Path.Combine(dir.Path, "Cars", Sim, "x_0000abcd.json")),
            "side file name");
    }

    [Test]
    public static void CarData_LoadRebindsTheIdentityToTheRequestedKeys()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var log = new RecordingLog();
        var store = new SpeedDialStore(dir.Path, log);
        string path = store.GetCarFilePath(Sim, Car);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"SimKey\":\"RFactor2\",\"CarKey\":\"Other\",\"DisplayName\":\"Old name\",\"Presets\":[{\"Name\":\"Kept\",\"Values\":{\"ABS\":4}}]}");

        SpeedDialCarData named = store.LoadCarData(Sim, Car, "New name");
        Assert.Equal(Sim, named.SimKey);
        Assert.Equal(Car, named.CarKey);
        Assert.Equal("New name", named.DisplayName, "given display name wins");
        Assert.Equal("Kept", named.Presets[0].Name, "content kept");
        Assert.True(log.Warnings.Exists(w => w.IndexOf("'Other'", StringComparison.Ordinal) >= 0), "foreign key reported");
        Assert.Equal("Old name", store.LoadCarData(Sim, Car, null).DisplayName, "stored display name kept without one");
    }

    [Test]
    public static void CarData_CorruptFileIsQuarantinedAndReplaced()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var log = new RecordingLog();
        var store = new SpeedDialStore(dir.Path, log, () => FixedUtc, null);
        string path = store.GetCarFilePath(Sim, Car);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"Presets\": [");

        SpeedDialCarData data = store.LoadCarData(Sim, Car, Car);

        Assert.Equal(0, data.Presets.Count, "new data");
        string quarantined = path + JsonFile.QuarantineInfix + FixedUtc.ToLocalTime().ToString(QuarantineTimestampFormat, CultureInfo.InvariantCulture);
        Assert.True(File.Exists(quarantined), "moved aside");
        Assert.False(File.Exists(path), "no longer loaded");
        Assert.Equal(1, log.Warnings.Count, "reported");

        data.Presets.Add(DialPreset.Create("New"));
        Assert.True(store.SaveCarData(data), "save");
        Assert.Equal("New", store.LoadCarData(Sim, Car, Car).Presets[0].Name, "replacement saved normally");
    }

    [Test]
    public static void CarData_UnreadableFileIsNeverReplacedBySessionSaves()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var log = new RecordingLog();
        var store = new SpeedDialStore(dir.Path, log, () => FixedUtc, new[] { 10 });
        SpeedDialCarData original = store.LoadCarData(Sim, Car, Car);
        original.Presets.Add(DialPreset.Create("Keep"));
        Assert.True(store.SaveCarData(original), "initial file");
        string path = store.GetCarFilePath(Sim, Car);
        string before = File.ReadAllText(path);
        string unsaved = SpeedDialStore.UnsavedPath(path);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            SpeedDialCarData session = store.LoadCarData(Sim, Car, Car);
            Assert.Equal(0, session.Presets.Count, "new data while unreadable");
            Assert.True(log.Errors.Count >= 1, "reported");

            // The lock goes away during the session; the session's saves must still not replace the unread file.
            locked.Dispose();
            session.Presets.Add(DialPreset.Create("Session"));
            store.SaveCarDataAsync(session);
            Assert.True(store.WaitForPendingWrites(WriteTimeout), "async save finished");
            Assert.True(store.SaveCarData(session), "End save");
        }

        Assert.Equal(before, File.ReadAllText(path), "real file untouched");
        Assert.True(File.Exists(unsaved), "side file written");
        Assert.Equal("Session", JsonFile.Deserialize<SpeedDialCarData>(File.ReadAllText(unsaved), out _).Presets[0].Name, "side file content");

        // Once the file can be read again it is used and saved normally.
        SpeedDialCarData next = store.LoadCarData(Sim, Car, Car);
        Assert.Equal("Keep", next.Presets[0].Name, "real file loaded");
        next.Presets.Add(DialPreset.Create("More"));
        Assert.True(store.SaveCarData(next), "normal save");
        Assert.Equal(2, store.LoadCarData(Sim, Car, Car).Presets.Count, "written to the real file");
    }

    [Test]
    public static void CarData_SecondLockedSessionContinuesFromTheSideFile()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new SpeedDialStore(dir.Path, NullLog.Instance, () => FixedUtc, new[] { 10 });
        SpeedDialCarData original = store.LoadCarData(Sim, Car, Car);
        original.Presets.Add(DialPreset.Create("Keep"));
        Assert.True(store.SaveCarData(original), "initial file");
        string path = store.GetCarFilePath(Sim, Car);
        string before = File.ReadAllText(path);
        string side = SpeedDialStore.UnsavedPath(path);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var session1 = new SpeedDialStore(dir.Path, NullLog.Instance, () => FixedUtc, new[] { 10 });
            SpeedDialCarData d1 = session1.LoadCarData(Sim, Car, Car);
            d1.Presets.Add(DialPreset.Create("Session 1"));
            Assert.True(session1.SaveCarData(d1), "session 1 saved to the side file");

            var session2 = new SpeedDialStore(dir.Path, NullLog.Instance, () => FixedUtc, new[] { 10 });
            SpeedDialCarData d2 = session2.LoadCarData(Sim, Car, Car);
            Assert.Equal(1, d2.Presets.Count, "continues from the side file");
            Assert.Equal("Session 1", d2.Presets[0].Name);
            d2.Presets.Add(DialPreset.Create("Session 2"));
            Assert.True(session2.SaveCarData(d2), "session 2 saved");
            Assert.Equal(2, JsonFile.Deserialize<SpeedDialCarData>(File.ReadAllText(side), out _).Presets.Count, "side file has both");

            // The side file locked as well: a timestamped side file, the old one stays as it is.
            using (new FileStream(side, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var session3 = new SpeedDialStore(dir.Path, NullLog.Instance, () => FixedUtc, new[] { 10 });
                SpeedDialCarData d3 = session3.LoadCarData(Sim, Car, Car);
                Assert.Equal(0, d3.Presets.Count, "new data");
                d3.Presets.Add(DialPreset.Create("Session 3"));
                Assert.True(session3.SaveCarData(d3), "session 3 saved");
            }

            Assert.Equal(2, JsonFile.Deserialize<SpeedDialCarData>(File.ReadAllText(side), out _).Presets.Count, "side file not overwritten");
            Assert.Equal(1, Directory.GetFiles(Path.GetDirectoryName(path), "*.unsaved-*.json").Length, "timestamped side file");
        }

        Assert.Equal(before, File.ReadAllText(path), "real file untouched");
    }

    [Test]
    public static void CarData_AsyncSavesCoalesceAndTheNewestWins()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new SpeedDialStore(dir.Path, NullLog.Instance);
        SpeedDialCarData data = store.LoadCarData(Sim, Car, Car);
        const int Saves = 25;

        for (int i = 0; i < Saves; i++)
        {
            data.DisplayName = "v" + i.ToString(CultureInfo.InvariantCulture);
            store.SaveCarDataAsync(data); // serialized now
        }

        data.DisplayName = "not saved";
        Assert.True(store.CarDataExists(Sim, Car), "queued or written counts as stored");
        Assert.True(store.WaitForPendingWrites(WriteTimeout), "writes finished");
        string json = File.ReadAllText(store.GetCarFilePath(Sim, Car));
        Assert.Equal("v" + (Saves - 1).ToString(CultureInfo.InvariantCulture), JsonFile.Deserialize<SpeedDialCarData>(json, out _).DisplayName, "newest content");
    }

    [Test]
    public static void CarData_LoadReturnsContentStillQueuedForARetry()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var log = new RecordingLog();
        var store = new SpeedDialStore(dir.Path, log, () => FixedUtc, new[] { 500 });
        SpeedDialCarData data = store.LoadCarData(Sim, Car, Car);
        Assert.True(store.SaveCarData(data), "initial file");
        string path = store.GetCarFilePath(Sim, Car);

        data.Presets.Add(DialPreset.Create("Queued"));
        SpeedDialCarData reloaded;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.SaveCarDataAsync(data);

            // Wait until the first attempt failed: the new content is now only in the queue, waiting for its retry.
            DateTime deadline = DateTime.UtcNow + WriteTimeout;
            while (log.ErrorCount == 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1);
            }

            Assert.Equal(1, log.ErrorCount, "first attempt failed");
            reloaded = store.LoadCarData(Sim, Car, Car);
        }

        Assert.Equal(1, reloaded.Presets.Count, "queued content, not the older (locked) file");
        Assert.Equal("Queued", reloaded.Presets[0].Name);
        Assert.True(store.WaitForPendingWrites(WriteTimeout), "writer finished");
        Assert.Equal("Queued", new SpeedDialStore(dir.Path, NullLog.Instance).LoadCarData(Sim, Car, Car).Presets[0].Name, "written by the retry");
    }

    [Test]
    public static void CarData_FailedAsyncWriteGivesUpAfterTheRetries()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var log = new RecordingLog();
        var store = new SpeedDialStore(dir.Path, log, () => FixedUtc, new[] { 10, 10 });
        SpeedDialCarData data = store.LoadCarData(Sim, Car, Car);
        Assert.True(store.SaveCarData(data), "initial file");
        string path = store.GetCarFilePath(Sim, Car);
        string before = File.ReadAllText(path);

        data.Presets.Add(DialPreset.Create("Lost"));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.SaveCarDataAsync(data);
            Assert.True(store.WaitForPendingWrites(WriteTimeout), "writer finished");
        }

        Assert.Equal(before, File.ReadAllText(path), "old content intact");
        Assert.Equal(4, log.Errors.Count, "three failed attempts and the give-up: " + string.Join(" | ", log.Errors));
        Assert.True(log.Errors[3].IndexOf("giving up", StringComparison.OrdinalIgnoreCase) >= 0, "give-up logged");
        Assert.True(store.SaveCarData(data), "the next save writes it");
        Assert.Equal("Lost", store.LoadCarData(Sim, Car, Car).Presets[0].Name);
    }

    // ---------------------------------------------------------------- export / import

    [Test]
    public static void ExportImport_RoundTripsTheCarData()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new SpeedDialStore(dir.Path, NullLog.Instance);
        SpeedDialCarData data = SpeedDialCarData.Create(Sim, Car, Car);
        DialPreset preset = DialPreset.Create("Night");
        preset.SetValue(DialChannel.Tc3, 4);
        data.Presets.Add(preset);
        data.SlotPresetIds[1] = preset.Id;
        data.GetLearning(DialChannel.Abs).RecordStep(1);
        data.LastUpdatedUtc = FixedUtc;
        string file = Path.Combine(dir.Path, "exports", "night.json");

        Assert.True(store.Export(data, file, out string exportError), "export: " + exportError);
        Assert.True(exportError == null, "no error");
        SpeedDialCarData imported = store.Import(file, out string importError);

        Assert.True(imported != null, "import: " + importError);
        Assert.True(importError == null, "no error");
        Assert.Equal(JsonFile.Serialize(data), JsonFile.Serialize(imported), "same content");
        Assert.False(store.Export(null, file, out exportError), "nothing to export");
        Assert.Equal("no car loaded.", exportError);
        Assert.False(store.Export(data, string.Empty, out exportError), "no file");
        Assert.Equal("no file selected.", exportError);
    }

    [Test]
    public static void Import_RejectsMissingForeignAndBrokenFiles()
    {
        using var dir = new PersistenceTests.TempDirectory();
        var store = new SpeedDialStore(dir.Path, NullLog.Instance);
        string foreign = Path.Combine(dir.Path, "haptics.json");
        File.WriteAllText(foreign, "{\"SchemaVersion\":1,\"SimKey\":\"LMU\",\"CarKey\":\"x\",\"SlipSensitivity\":100}");
        string broken = Path.Combine(dir.Path, "broken.json");
        File.WriteAllText(broken, "{\"SchemaVersion\":1,\"Presets\":[");
        string array = Path.Combine(dir.Path, "array.json");
        File.WriteAllText(array, "[1,2]");

        Assert.True(store.Import(string.Empty, out string error) == null && error == "no file selected.", "no file: " + error);
        Assert.True(store.Import(Path.Combine(dir.Path, "missing.json"), out error) == null && error == "file not found.", "missing: " + error);
        Assert.True(store.Import(foreign, out error) == null && error.IndexOf("'Presets'", StringComparison.Ordinal) >= 0, "foreign: " + error);
        Assert.True(store.Import(broken, out error) == null && error.StartsWith("not a valid Speed Dial file", StringComparison.Ordinal), "broken: " + error);
        Assert.True(store.Import(array, out error) == null && error.StartsWith("not a valid Speed Dial file", StringComparison.Ordinal), "array: " + error);
    }

    [Test]
    public static void AppendPresets_RenewsCollidingIdsAndRespectsTheLimit()
    {
        SpeedDialCarData target = SpeedDialCarData.Create(Sim, Car, Car);
        DialPreset existing = DialPreset.Create("Existing");
        target.Presets.Add(existing);
        target.SlotPresetIds[0] = existing.Id;
        SpeedDialCarData source = SpeedDialCarData.Create(Sim, "Other", "Other");
        DialPreset sameId = DialPreset.Create("Same id");
        sameId.Id = existing.Id.ToUpperInvariant();
        DialPreset fresh = DialPreset.Create("Fresh");
        fresh.SetValue(DialChannel.Abs, 4);
        source.Presets.Add(sameId);
        source.Presets.Add(fresh);
        source.SlotPresetIds[0] = fresh.Id;
        source.SelectedPresetId = fresh.Id;

        int added = SpeedDialStore.AppendPresets(target, source, out int skipped);

        Assert.Equal(2, added);
        Assert.Equal(0, skipped);
        Assert.Equal(3, target.Presets.Count);
        Assert.False(string.Equals(target.Presets[1].Id, existing.Id, StringComparison.OrdinalIgnoreCase), "colliding id renewed");
        Assert.Equal("Same id", target.Presets[1].Name);
        Assert.Equal(fresh.Id, target.Presets[2].Id, "free id kept");
        Assert.False(ReferenceEquals(fresh, target.Presets[2]), "copied");
        Assert.True(target.Presets[2].TryGetValue(DialChannel.Abs, out double abs) && abs == 4.0, "values copied");
        Assert.Equal(existing.Id, target.SlotPresetIds[0], "target slots untouched");
        Assert.True(target.SelectedPresetId == null, "source selection ignored");

        var full = SpeedDialCarData.Create(Sim, Car, Car);
        for (int i = 0; i < SpeedDialCarData.MaxPresets - 1; i++)
        {
            full.Presets.Add(DialPreset.Create("P" + i.ToString(CultureInfo.InvariantCulture)));
        }

        source.Presets.Add(DialPreset.Create("Third"));
        added = SpeedDialStore.AppendPresets(full, source, out skipped);
        Assert.Equal(1, added, "one place left");
        Assert.Equal(2, skipped, "rest skipped");
        Assert.Equal(SpeedDialCarData.MaxPresets, full.Presets.Count, "limit");
        Assert.Equal(0, SpeedDialStore.AppendPresets(null, source, out _), "no target");
    }

    /// <summary>ILog that records messages per level (thread-safe: writers log from the thread pool).</summary>
    private sealed class RecordingLog : ILog
    {
        public List<string> Infos { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public List<string> Errors { get; } = new List<string>();

        /// <summary>Errors so far (safe to poll while writers log from the thread pool).</summary>
        public int ErrorCount
        {
            get
            {
                lock (Errors)
                {
                    return Errors.Count;
                }
            }
        }

        public void Info(string message) => Add(Infos, message);

        public void Warn(string message) => Add(Warnings, message);

        public void Error(string message) => Add(Errors, message);

        private static void Add(List<string> list, string message)
        {
            lock (list)
            {
                list.Add(message);
            }
        }
    }
}
