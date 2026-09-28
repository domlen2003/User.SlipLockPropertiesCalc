using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using User.SlipLockPropertiesCalc.Balance;
using User.SlipLockPropertiesCalc.Core;
using User.SlipLockPropertiesCalc.Settings;

namespace User.SlipLockPropertiesCalc.Tests;

/// <summary>Tests of <see cref="JsonFile"/>, <see cref="CarProfileStore"/> and <see cref="SaveScheduler"/>.</summary>
internal sealed class PersistenceTests
{
    private const string Sim = "LMU";
    private const string Car = "Ligier JS P320";

    private static readonly DateTime FixedUtc = new DateTime(2026, 9, 27, 21, 30, 15, DateTimeKind.Utc);

    // ---------------------------------------------------------------- CarProfileStore: load / save

    [Test]
    public void Load_MissingFile_ReturnsDefaultProfileWithIdentity()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);

        CarProfile profile = store.Load(Sim, Car, "Ligier (display)", "LMP3");

        Assert.Equal(Sim, profile.SimKey);
        Assert.Equal(Car, profile.CarKey);
        Assert.Equal("Ligier (display)", profile.DisplayName);
        Assert.Equal("LMP3", profile.CarClass);
        Assert.Equal(CarProfile.DefaultSensitivity, profile.SlipSensitivity);
        Assert.Equal(CarProfile.DefaultSensitivity, profile.LockSensitivity);
        Assert.Equal(CarProfile.DefaultSensitivity, profile.UndersteerSensitivity);
        Assert.Equal(CarProfile.DefaultSensitivity, profile.OversteerSensitivity);
        Assert.True(profile.Overrides != null && profile.Learned != null, "members initialized");
        Assert.False(File.Exists(store.GetProfilePath(Sim, Car)), "a new profile is not written until saved");
    }

    [Test]
    public void SaveLoad_RoundTripsAllFields()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance, () => FixedUtc);

        CarProfile original = store.Load(Sim, Car, Car, "LMP3");
        original.SlipSensitivity = 135;
        original.LockSensitivity = 420;
        original.UndersteerSensitivity = 55;
        original.OversteerSensitivity = 10;
        original.LearningLocked = true;
        original.Overrides.G = 0.031;
        original.Overrides.WheelbaseM = 2.95;
        original.Overrides.K = null;
        original.Overrides.Theta0Deg = -1.25;
        original.Overrides.ClassPreset = BalanceClassPreset.FormulaPrototype;
        FillDeterministically(original.Learned, depth: 0);

        Assert.True(store.Save(original), "save succeeds");
        CarProfile loaded = new CarProfileStore(dir.Path, NullLog.Instance).Load(Sim, Car, string.Empty, string.Empty);

        Assert.Equal(FixedUtc, loaded.LastUpdatedUtc, "time stamp");
        Assert.Equal(DateTimeKind.Utc, loaded.LastUpdatedUtc.Kind, "time stamp kind");
        Assert.Equal(135.0, loaded.SlipSensitivity);
        Assert.Equal(420.0, loaded.LockSensitivity);
        Assert.Equal(55.0, loaded.UndersteerSensitivity);
        Assert.Equal(10.0, loaded.OversteerSensitivity);
        Assert.True(loaded.LearningLocked, "learning lock");
        Assert.Equal<double?>(0.031, loaded.Overrides.G);
        Assert.Equal<double?>(null, loaded.Overrides.K, "cleared override stays null");
        Assert.Equal<BalanceClassPreset?>(BalanceClassPreset.FormulaPrototype, loaded.Overrides.ClassPreset);
        Assert.Equal("LMP3", loaded.CarClass, "stored class kept when the caller passes none");

        // Whole-object comparison covers every field of the learned state, whatever it contains today.
        Assert.Equal(JsonFile.Serialize(original), JsonFile.Serialize(loaded), "serialized profiles");
    }

    [Test]
    public void Save_WritesEnumsAsStringsAndNullsExplicitly()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);
        CarProfile profile = store.Load(Sim, Car, Car, "GT3");
        profile.Overrides.ClassPreset = BalanceClassPreset.RallyLoose;

        Assert.True(store.Save(profile), "save succeeds");
        string text = File.ReadAllText(store.GetProfilePath(Sim, Car));
        JObject json = JObject.Parse(text);

        Assert.Equal("RallyLoose", (string)json["Overrides"]["ClassPreset"], "enum written by name");
        Assert.Equal(JTokenType.Null, json["Overrides"]["G"].Type, "null override written");
        Assert.True(text.IndexOf("\n  \"", StringComparison.Ordinal) >= 0, "indented");
        Assert.False(text.Length > 0 && text[0] == '﻿', "no BOM");
    }

    [Test]
    public void Load_NormalizesOutOfRangeValues()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);
        string path = store.GetProfilePath(Sim, Car);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"CarKey\": \"" + Car + "\", \"SlipSensitivity\": 5000, \"LockSensitivity\": -3, "
            + "\"UndersteerSensitivity\": \"NaN\", \"OversteerSensitivity\": 250, \"Overrides\": null, \"Learned\": null, \"DisplayName\": null }");

        CarProfile profile = store.Load(Sim, Car, string.Empty, string.Empty);

        Assert.Equal(CarProfile.MaxSensitivity, profile.SlipSensitivity, "clamped high");
        Assert.Equal(CarProfile.MinSensitivity, profile.LockSensitivity, "clamped low");
        Assert.Equal(CarProfile.DefaultSensitivity, profile.UndersteerSensitivity, "NaN -> default");
        Assert.Equal(250.0, profile.OversteerSensitivity, "in range kept");
        Assert.True(profile.Overrides != null && profile.Learned != null, "null members repaired");
        Assert.Equal(Car, profile.DisplayName, "empty display name falls back to the car key");
    }

    [Test]
    public void Load_UnconvertibleValue_KeepsRestOfFile()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        var store = new CarProfileStore(dir.Path, log);
        string path = store.GetProfilePath(Sim, Car);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"CarKey\": \"" + Car + "\", \"SlipSensitivity\": 175, "
            + "\"Overrides\": { \"G\": 0.02, \"ClassPreset\": \"SomeFutureClass\" }, \"LockSensitivity\": 60 }");

        CarProfile profile = store.Load(Sim, Car, string.Empty, string.Empty);

        Assert.Equal(175.0, profile.SlipSensitivity, "value before the bad one");
        Assert.Equal(60.0, profile.LockSensitivity, "value after the bad one");
        Assert.Equal<double?>(0.02, profile.Overrides.G, "sibling of the bad value");
        Assert.Equal<BalanceClassPreset?>(null, profile.Overrides.ClassPreset, "bad value keeps its default");
        Assert.True(File.Exists(path), "not quarantined");
        Assert.True(log.Warnings.Count == 1, "skipped value logged");
    }

    [Test]
    public void Load_StoredForOtherKey_UsesRequestedIdentity()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);
        string path = store.GetProfilePath(Sim, Car);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"SimKey\": \"X\", \"CarKey\": \"Other\", \"CarClass\": \"Old\", \"SlipSensitivity\": 200 }");

        CarProfile profile = store.Load(Sim, Car, "Display", "LMP3");

        Assert.Equal(Sim, profile.SimKey);
        Assert.Equal(Car, profile.CarKey);
        Assert.Equal("Display", profile.DisplayName);
        Assert.Equal("LMP3", profile.CarClass, "fresh class wins");
        Assert.Equal(200.0, profile.SlipSensitivity, "content kept");
    }

    // ---------------------------------------------------------------- file names

    [Test]
    public void GetProfilePath_SanitizesAndHashes()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);

        string path = store.GetProfilePath("LMU", "GT3: Iron/Lynx \"#61\"?");
        string fileName = Path.GetFileName(path);

        Assert.Equal(Path.Combine(dir.Path, CarProfileStore.CarsFolderName, "LMU"), Path.GetDirectoryName(path), "sim folder");
        Assert.True(fileName.StartsWith("GT3_ Iron_Lynx _#61__", StringComparison.Ordinal), "invalid chars replaced: " + fileName);
        Assert.True(fileName.EndsWith(CarProfileStore.FileExtension, StringComparison.Ordinal), "extension");
        Assert.Equal(-1, fileName.IndexOfAny(Path.GetInvalidFileNameChars()), "no invalid chars");
        Assert.Equal(path, store.GetProfilePath("LMU", "GT3: Iron/Lynx \"#61\"?"), "deterministic");
    }

    [Test]
    public void GetProfilePath_KeysDifferingOnlyByInvalidCharsOrCase_MapToDifferentFiles()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);
        string[] keys = { "A/B", "A\\B", "A:B", "A_B", "A*B", "a_b", "A_B " };

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in keys)
        {
            Assert.True(paths.Add(store.GetProfilePath(Sim, key)), "unique path for '" + key + "'");
        }

        // And the files really are separate.
        for (int i = 0; i < keys.Length; i++)
        {
            CarProfile profile = store.Load(Sim, keys[i], keys[i], string.Empty);
            profile.SlipSensitivity = 100 + i;
            Assert.True(store.Save(profile), "save " + keys[i]);
        }

        for (int i = 0; i < keys.Length; i++)
        {
            Assert.Equal(100.0 + i, store.Load(Sim, keys[i], keys[i], string.Empty).SlipSensitivity, "content of '" + keys[i] + "'");
        }
    }

    [Test]
    public void BuildCarFileStem_TruncatesLongKeysAndHandlesSpecialNames()
    {
        string longKey = new string('x', 300);
        string stem = CarProfileStore.BuildCarFileStem(longKey);
        Assert.Equal(CarProfileStore.MaxFileNameStemLength + 9, stem.Length, "80 chars + '_' + 8 hex");
        Assert.True(stem.StartsWith(new string('x', 80) + "_", StringComparison.Ordinal), "readable prefix");
        Assert.False(CarProfileStore.BuildCarFileStem(longKey) == CarProfileStore.BuildCarFileStem(longKey + "y"), "long keys still unique");

        Assert.True(CarProfileStore.BuildCarFileStem("CON").StartsWith("_CON_", StringComparison.Ordinal), "reserved device name");
        Assert.True(CarProfileStore.BuildCarFileStem("  ").StartsWith("unnamed_", StringComparison.Ordinal), "blank key");
        Assert.True(CarProfileStore.BuildCarFileStem(null).StartsWith("unnamed_", StringComparison.Ordinal), "null key");
        Assert.True(CarProfileStore.BuildCarFileStem("car..").StartsWith("car_", StringComparison.Ordinal), "trailing dots trimmed");
        Assert.Equal("LMU", CarProfileStore.SanitizeSegment(" LMU. "), "segment trimmed");
        Assert.Equal("_NUL.txt", CarProfileStore.SanitizeSegment("NUL.txt"), "reserved with extension");

        // Surrogate pair straddling the cut is not split.
        string emoji = new string('y', 79) + "😀" + "tail";
        string cut = CarProfileStore.BuildCarFileStem(emoji);
        Assert.Equal(new string('y', 79) + "_", cut.Substring(0, 80), "cut before the pair, no dangling high surrogate");
        Assert.Equal(79 + 9, cut.Length, "shortened by one char");
    }

    [Test]
    public void Fnv1a_MatchesReferenceVectors()
    {
        Assert.Equal(0x811c9dc5u, CarProfileStore.Fnv1a(string.Empty), "empty");
        Assert.Equal(0xe40c292cu, CarProfileStore.Fnv1a("a"), "a");
        Assert.Equal(0xbf9cf968u, CarProfileStore.Fnv1a("foobar"), "foobar");
    }

    // ---------------------------------------------------------------- corrupt files

    [Test]
    public void Load_CorruptFile_IsQuarantinedAndDefaultsReturned()
    {
        string[] corruptContents =
        {
            "{ \"SchemaVersion\": 1, \"SlipSensitivity\": 2",   // truncated
            "this is not json",
            "[1, 2, 3]",                                          // wrong root type
            "{ \"SlipSensitivity\": 200 } trailing",              // damaged tail
            string.Empty,
        };

        foreach (string content in corruptContents)
        {
            using var dir = new TempDirectory();
            var log = new RecordingLog();
            var store = new CarProfileStore(dir.Path, log, () => FixedUtc);
            string path = store.GetProfilePath(Sim, Car);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);

            CarProfile profile = store.Load(Sim, Car, Car, string.Empty);

            string expectedBad = path + JsonFile.QuarantineInfix + FixedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            Assert.Equal(CarProfile.DefaultSensitivity, profile.SlipSensitivity, "defaults for '" + content + "'");
            Assert.False(File.Exists(path), "original moved away for '" + content + "'");
            Assert.True(File.Exists(expectedBad), "quarantine file for '" + content + "'");
            Assert.Equal(content, File.ReadAllText(expectedBad), "quarantined content preserved");
            Assert.Equal(1, log.Warnings.Count, "logged once");

            // Saving the fresh profile works and a second corruption in the same second gets a distinct name.
            Assert.True(store.Save(profile), "save after quarantine");
            File.WriteAllText(path, "{");
            store.Load(Sim, Car, Car, string.Empty);
            Assert.True(File.Exists(expectedBad + "-1"), "second quarantine name");
        }
    }

    // ---------------------------------------------------------------- atomic writes

    [Test]
    public void WriteAllTextAtomic_FailureLeavesOldFileAndNoTemp()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "sub", "file.json");
        JsonFile.WriteAllTextAtomic(path, "{\"v\":1}");
        Assert.Equal("{\"v\":1}", File.ReadAllText(path), "created with directory");

        // An exclusive handle (antivirus, editor, backup tool) makes the swap fail.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => JsonFile.WriteAllTextAtomic(path, "{\"v\":2}"), "swap fails");
        }

        Assert.Equal("{\"v\":1}", File.ReadAllText(path), "old content intact");
        Assert.False(File.Exists(path + JsonFile.TempSuffix), "temp file removed");

        // A stale temp file from a crash does not block the next write.
        File.WriteAllText(path + JsonFile.TempSuffix, "partial garb");
        JsonFile.WriteAllTextAtomic(path, "{\"v\":3}");
        Assert.Equal("{\"v\":3}", File.ReadAllText(path), "replaced");
        Assert.False(File.Exists(path + JsonFile.TempSuffix), "no temp after success");
    }

    [Test]
    public void Save_WhenFileLocked_ReturnsFalseLogsAndKeepsOldContent()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        var store = new CarProfileStore(dir.Path, log);
        CarProfile profile = store.Load(Sim, Car, Car, string.Empty);
        profile.SlipSensitivity = 150;
        Assert.True(store.Save(profile), "first save");
        string path = store.GetProfilePath(Sim, Car);
        string before = File.ReadAllText(path);

        profile.SlipSensitivity = 300;
        bool saved;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            saved = store.Save(profile);
        }

        Assert.False(saved, "save reports failure");
        Assert.Equal(1, log.Errors.Count, "failure logged");
        Assert.Equal(before, File.ReadAllText(path), "old content intact");
        Assert.False(File.Exists(path + JsonFile.TempSuffix), "no temp file");

        // Once the lock is gone the next save goes through.
        Assert.True(store.Save(profile), "retry");
        Assert.Equal(300.0, store.Load(Sim, Car, Car, string.Empty).SlipSensitivity, "new content");
    }

    // ---------------------------------------------------------------- async saves

    [Test]
    public void SaveAsync_ManySaves_LastWriteWins()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);
        CarProfile profile = store.Load(Sim, Car, Car, string.Empty);

        for (int i = 0; i < 300; i++)
        {
            profile.SlipSensitivity = 10 + i;
            store.SaveAsync(profile);
        }

        Assert.True(store.WaitForPendingWrites(TimeSpan.FromSeconds(10)), "writes finished");
        Assert.Equal(309.0, store.Load(Sim, Car, Car, string.Empty).SlipSensitivity, "newest content on disk");
        Assert.False(File.Exists(store.GetProfilePath(Sim, Car) + JsonFile.TempSuffix), "no temp file");
    }

    [Test]
    public void SaveAsync_SerializesOnCallerThread()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);
        CarProfile profile = store.Load(Sim, Car, Car, string.Empty);

        profile.SlipSensitivity = 222;
        store.SaveAsync(profile);

        // Mutating right after the call must not leak into the queued write.
        profile.SlipSensitivity = 444;
        Assert.True(store.WaitForPendingWrites(TimeSpan.FromSeconds(10)), "writes finished");
        Assert.Equal(222.0, store.Load(Sim, Car, Car, string.Empty).SlipSensitivity, "snapshot taken at call time");
    }

    [Test]
    public void Save_AfterQueuedAsyncSaves_IsNotOverwrittenByOlderContent()
    {
        for (int round = 0; round < 20; round++)
        {
            using var dir = new TempDirectory();
            var store = new CarProfileStore(dir.Path, NullLog.Instance);
            CarProfile profile = store.Load(Sim, Car, Car, string.Empty);

            for (int i = 0; i < 20; i++)
            {
                profile.LockSensitivity = 20 + i;
                store.SaveAsync(profile);
            }

            profile.LockSensitivity = 480;
            Assert.True(store.Save(profile), "sync save");
            Assert.True(store.WaitForPendingWrites(TimeSpan.FromSeconds(10)), "writes finished");
            Assert.Equal(480.0, store.Load(Sim, Car, Car, string.Empty).LockSensitivity, "sync (newest) content wins, round " + round);
        }
    }

    [Test]
    public void SaveAsync_ConcurrentReaderNeverSeesPartialFile()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);
        CarProfile profile = store.Load(Sim, Car, Car, string.Empty);
        FillDeterministically(profile.Learned, depth: 0);
        Assert.True(store.Save(profile), "initial file");
        string path = store.GetProfilePath(Sim, Car);

        int badReads = 0;
        int goodReads = 0;
        using var stop = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    continue; // sharing violation during the swap: fine, the reader just retries
                }
                catch (UnauthorizedAccessException)
                {
                    continue; // File.Replace briefly marks the target for deletion
                }

                try
                {
                    JsonFile.ParseObject(text);
                    goodReads++;
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    badReads++;
                }
            }
        });
        reader.Start();

        for (int i = 0; i < 200; i++)
        {
            profile.OversteerSensitivity = 10 + i;
            store.SaveAsync(profile);
            if (i % 20 == 0)
            {
                Thread.Sleep(1);
            }
        }

        Assert.True(store.WaitForPendingWrites(TimeSpan.FromSeconds(10)), "writes finished");
        stop.Set();
        reader.Join();

        Assert.Equal(0, badReads, "partial/garbled reads");
        Assert.True(goodReads > 0, "reader observed the file");
        Assert.Equal(209.0, store.Load(Sim, Car, Car, string.Empty).OversteerSensitivity, "final content");
    }

    [Test]
    public void SaveAsync_WriteFailure_IsRetriedUntilTheFileIsFree()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        var store = new CarProfileStore(dir.Path, log, () => FixedUtc, new[] { 100, 200, 400 });
        CarProfile profile = store.Load(Sim, Car, Car, string.Empty);
        Assert.True(store.Save(profile), "initial file");
        string path = store.GetProfilePath(Sim, Car);

        profile.LockSensitivity = 220;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.SaveAsync(profile);
            Thread.Sleep(250); // first attempt and first retry fail
        }

        Assert.True(store.WaitForPendingWrites(TimeSpan.FromSeconds(5)), "writer finished");
        Assert.True(log.Errors.Count >= 1, "failures logged");
        Assert.Equal(220.0, store.Load(Sim, Car, Car, string.Empty).LockSensitivity, "content written by a retry");
    }

    [Test]
    public void SaveAsync_WriteFailure_GivesUpAfterTheRetries()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        var store = new CarProfileStore(dir.Path, log, () => FixedUtc, new[] { 10, 10 });
        CarProfile profile = store.Load(Sim, Car, Car, string.Empty);
        Assert.True(store.Save(profile), "initial file");
        string path = store.GetProfilePath(Sim, Car);
        string before = File.ReadAllText(path);

        profile.LockSensitivity = 220;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.SaveAsync(profile);
            Assert.True(store.WaitForPendingWrites(TimeSpan.FromSeconds(5)), "writer finished");
        }

        Assert.Equal(before, File.ReadAllText(path), "old content intact");
        Assert.Equal(4, log.Errors.Count, "three failed attempts and the final give-up: " + string.Join(" | ", log.Errors));
        Assert.True(log.Errors[3].Contains("giving up"), "give-up logged");
    }

    [Test]
    public void Load_ReturnsContentStillQueuedBySaveAsync()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        var store = new CarProfileStore(dir.Path, log, () => FixedUtc, new[] { 200 });
        CarProfile profile = store.Load(Sim, Car, Car, string.Empty);
        Assert.True(store.Save(profile), "initial file");
        string path = store.GetProfilePath(Sim, Car);

        profile.SlipSensitivity = 175;
        CarProfile reloaded;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.SaveAsync(profile); // fails and waits for its retry: the new content is only in the queue
            Thread.Sleep(50);
            reloaded = store.Load(Sim, Car, Car, string.Empty);
        }

        Assert.Equal(175.0, reloaded.SlipSensitivity, "queued content, not the older (or unreadable) file");
        Assert.True(store.WaitForPendingWrites(TimeSpan.FromSeconds(5)), "writer finished");
        Assert.Equal(175.0, store.Load(Sim, Car, Car, string.Empty).SlipSensitivity, "written by the retry");
    }

    [Test]
    public void Load_UnreadableFile_SessionSavesNeverReplaceIt()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        var store = new CarProfileStore(dir.Path, log, () => FixedUtc, new[] { 10 });
        CarProfile profile = store.Load(Sim, Car, Car, string.Empty);
        profile.SlipSensitivity = 250;
        Assert.True(store.Save(profile), "save");
        string path = store.GetProfilePath(Sim, Car);
        string before = File.ReadAllText(path);
        string unsaved = CarProfileStore.UnsavedPath(path);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            CarProfile defaults = store.Load(Sim, Car, Car, string.Empty);
            Assert.Equal(CarProfile.DefaultSensitivity, defaults.SlipSensitivity, "defaults while unreadable");

            // The lock is released during the session; the session's saves must still not replace the file.
            locked.Dispose();
            defaults.LockSensitivity = 300;
            store.SaveAsync(defaults);
            Assert.True(store.WaitForPendingWrites(TimeSpan.FromSeconds(5)), "async save finished");
            Assert.True(store.Save(defaults), "End save");
        }

        Assert.Equal(before, File.ReadAllText(path), "real profile untouched");
        Assert.True(File.Exists(unsaved), "session saved to the side file");
        Assert.Equal(300.0, JsonFile.Deserialize<CarProfile>(File.ReadAllText(unsaved), out _).LockSensitivity, "side file content");

        // Once the file can be read again it is used and saved normally.
        CarProfile next = store.Load(Sim, Car, Car, string.Empty);
        Assert.Equal(250.0, next.SlipSensitivity, "real profile loaded");
        next.SlipSensitivity = 260;
        Assert.True(store.Save(next), "normal save");
        Assert.Equal(260.0, store.Load(Sim, Car, Car, string.Empty).SlipSensitivity, "written to the real file");
    }

    // ---------------------------------------------------------------- export / import

    [Test]
    public void ExportImport_RoundTrips()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);
        CarProfile profile = store.Load(Sim, Car, Car, "LMP3");
        profile.UndersteerSensitivity = 333;
        profile.Overrides.TauYawS = 0.15;
        profile.Overrides.ClassPreset = BalanceClassPreset.GT;
        FillDeterministically(profile.Learned, depth: 0);
        string exportPath = Path.Combine(dir.Path, "export", "my car.json");

        Assert.True(store.Export(profile, exportPath, out string exportError), "export: " + exportError);
        CarProfile imported = store.Import(exportPath, out string importError);

        Assert.True(imported != null, "import: " + importError);
        Assert.Equal<string>(null, importError, "no error");
        Assert.Equal(JsonFile.Serialize(profile), JsonFile.Serialize(imported), "identical content");
    }

    [Test]
    public void Import_RejectsForeignAndBrokenFiles()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);

        string foreign = Path.Combine(dir.Path, "foreign.json");
        File.WriteAllText(foreign, "{ \"SlipThrottleBlend\": 20, \"GameCapabilities\": {} }");
        Assert.True(store.Import(foreign, out string foreignError) == null, "foreign JSON rejected");
        Assert.True(foreignError.IndexOf("Not a SlipLock car profile", StringComparison.Ordinal) >= 0, foreignError);

        string broken = Path.Combine(dir.Path, "broken.json");
        File.WriteAllText(broken, "{ \"SchemaVersion\": 1, \"CarKey\": ");
        Assert.True(store.Import(broken, out string brokenError) == null, "broken JSON rejected");
        Assert.True(!string.IsNullOrEmpty(brokenError), "error text");
        Assert.True(File.Exists(broken), "import never quarantines user files");

        Assert.True(store.Import(Path.Combine(dir.Path, "missing.json"), out string missingError) == null, "missing rejected");
        Assert.Equal("File not found.", missingError);

        string outOfRange = Path.Combine(dir.Path, "range.json");
        File.WriteAllText(outOfRange, "{ \"SchemaVersion\": 1, \"CarKey\": \"x\", \"LockSensitivity\": 9999, \"Overrides\": null }");
        CarProfile normalized = store.Import(outOfRange, out _);
        Assert.Equal(CarProfile.MaxSensitivity, normalized.LockSensitivity, "import normalizes");
        Assert.True(normalized.Overrides != null, "import repairs nulls");
    }

    [Test]
    public void Export_ToInvalidLocation_ReportsError()
    {
        using var dir = new TempDirectory();
        var store = new CarProfileStore(dir.Path, NullLog.Instance);
        CarProfile profile = store.Load(Sim, Car, Car, string.Empty);
        string blocker = Path.Combine(dir.Path, "iam-a-file");
        File.WriteAllText(blocker, "x");

        Assert.False(store.Export(profile, Path.Combine(blocker, "profile.json"), out string error), "export fails");
        Assert.True(!string.IsNullOrEmpty(error), "error text");
        Assert.False(store.Export(null, Path.Combine(dir.Path, "p.json"), out _), "no profile");
    }

    // ---------------------------------------------------------------- JsonFile conventions

    [Test]
    public void JsonFile_RoundTripsSpecialValuesAndReplacesCollections()
    {
        var dto = new SampleDto
        {
            NotANumber = double.NaN,
            Infinite = double.PositiveInfinity,
            Text = "2025-01-01T00:00:00",
            Mode = BalanceMode.DirectOnly,
            Numbers = new List<int> { 7 },
            Values = new[] { 0.1, double.NaN, -2.5e-9 },
        };

        string json = JsonFile.Serialize(dto);
        SampleDto back = JsonFile.Deserialize<SampleDto>(json, out int skipped);

        Assert.Equal(0, skipped, "skipped");
        Assert.True(double.IsNaN(back.NotANumber), "NaN");
        Assert.True(double.IsPositiveInfinity(back.Infinite), "Infinity");
        Assert.Equal("2025-01-01T00:00:00", back.Text, "date-like text untouched");
        Assert.Equal(BalanceMode.DirectOnly, back.Mode);
        Assert.Equal(1, back.Numbers.Count, "collection replaced, not appended to the initializer");
        Assert.Equal(7, back.Numbers[0]);
        Assert.Equal(-2.5e-9, back.Values[2], "round-trip precision");
        Assert.True(json.IndexOf("\"DirectOnly\"", StringComparison.Ordinal) >= 0, "enum as string");

        // Numbers are written with the invariant culture even under a comma-decimal culture.
        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string german = JsonFile.Serialize(new SampleDto { Infinite = 1.5 });
            Assert.True(german.IndexOf("1.5", StringComparison.Ordinal) >= 0, "invariant decimal point");
            Assert.Equal(1.5, JsonFile.Deserialize<SampleDto>(german, out _).Infinite, "invariant parse");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Test]
    public void JsonFile_TryRead_ReportsStatus()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "x.json");

        Assert.Equal(JsonReadStatus.Missing, JsonFile.TryRead(path, out SampleDto missing, out _, out _), "missing");
        Assert.True(missing == null, "no value");

        File.WriteAllText(path, "{ \"Text\": \"hi\" }");
        Assert.Equal(JsonReadStatus.Loaded, JsonFile.TryRead(path, out SampleDto loaded, out _, out string loadError), "loaded");
        Assert.Equal("hi", loaded.Text);
        Assert.Equal<string>(null, loadError);

        File.WriteAllText(path, "{ \"Text\": ");
        Assert.Equal(JsonReadStatus.Corrupt, JsonFile.TryRead(path, out SampleDto _, out _, out string corruptError), "corrupt");
        Assert.True(!string.IsNullOrEmpty(corruptError), "corrupt error text");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(JsonReadStatus.IoError, JsonFile.TryRead(path, out SampleDto _, out _, out _), "locked");
        }
    }

    [Test]
    public void Load_LockedFile_ReturnsDefaultsWithoutQuarantine()
    {
        using var dir = new TempDirectory();
        var log = new RecordingLog();
        var store = new CarProfileStore(dir.Path, log);
        CarProfile profile = store.Load(Sim, Car, Car, string.Empty);
        profile.SlipSensitivity = 250;
        Assert.True(store.Save(profile), "save");
        string path = store.GetProfilePath(Sim, Car);

        CarProfile whileLocked;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            whileLocked = store.Load(Sim, Car, Car, string.Empty);
        }

        Assert.Equal(CarProfile.DefaultSensitivity, whileLocked.SlipSensitivity, "defaults while unreadable");
        Assert.Equal(1, log.Errors.Count, "logged");
        Assert.Equal(250.0, store.Load(Sim, Car, Car, string.Empty).SlipSensitivity, "file untouched");
    }

    // ---------------------------------------------------------------- SettingsWriter

    [Test]
    public void SettingsWriter_CopiesOnTheCallerThreadAndWritesOffIt()
    {
        var written = new List<double>();
        int writerThread = -1;
        var writer = new SettingsWriter(copy =>
        {
            writerThread = Thread.CurrentThread.ManagedThreadId;
            lock (written)
            {
                written.Add(copy.SlipThreshold);
            }
        }, new RecordingLog());
        var live = new PluginSettings { SlipThreshold = 12 };

        writer.SaveAsync(live);
        live.SlipThreshold = 99; // edited right after: the queued copy must not change
        Assert.True(writer.WaitForPendingWrites(TimeSpan.FromSeconds(5)), "writer finished");

        Assert.Equal(12.0, written[written.Count - 1], "the copy taken at SaveAsync was written");
        Assert.True(writerThread != Thread.CurrentThread.ManagedThreadId, "written on another thread");
    }

    [Test]
    public void SettingsWriter_NewestWinsAndSaveSupersedesQueuedCopies()
    {
        var written = new List<double>();
        using var gate = new ManualResetEventSlim(false);
        var writer = new SettingsWriter(copy =>
        {
            if (copy.SlipThreshold == 1)
            {
                gate.Wait(TimeSpan.FromSeconds(5)); // hold the first write while newer copies arrive
            }

            lock (written)
            {
                written.Add(copy.SlipThreshold);
            }
        }, new RecordingLog());
        var live = new PluginSettings { SlipThreshold = 1 };
        writer.SaveAsync(live);
        Thread.Sleep(50);
        for (int i = 2; i <= 5; i++)
        {
            live.SlipThreshold = i;
            writer.SaveAsync(live); // coalesce: only the newest queued copy may be written
        }

        live.SlipThreshold = 9;
        var sync = new Thread(() => writer.Save(live)); // shutdown save while the first write still runs
        sync.Start();
        Thread.Sleep(50);
        gate.Set();
        sync.Join();
        Assert.True(writer.WaitForPendingWrites(TimeSpan.FromSeconds(5)), "writer finished");

        Assert.Equal(9.0, written[written.Count - 1], "the synchronous save is the last write: " + string.Join(",", written));
        Assert.False(written.Contains(2) || written.Contains(3) || written.Contains(4), "superseded copies skipped");
    }

    [Test]
    public void SettingsWriter_FailureIsLoggedNotThrown()
    {
        var log = new RecordingLog();
        var writer = new SettingsWriter(_ => throw new IOException("disk full"), log);
        writer.SaveAsync(new PluginSettings());
        Assert.True(writer.WaitForPendingWrites(TimeSpan.FromSeconds(5)), "writer finished");
        Assert.False(writer.Save(new PluginSettings()), "synchronous save reports failure");
        Assert.Equal(2, log.Errors.Count, "both failures logged");
    }

    // ---------------------------------------------------------------- SaveScheduler

    [Test]
    public void SaveScheduler_SettingsDebounced()
    {
        int saves = 0;
        var scheduler = new SaveScheduler(() => saves++, () => { });

        scheduler.Tick(0);
        scheduler.MarkSettingsDirty(10.0);
        scheduler.Tick(11.9);
        Assert.Equal(0, saves, "not before 2 s");
        scheduler.MarkSettingsDirty(11.5); // still dragging a slider
        scheduler.Tick(13.4);
        Assert.Equal(0, saves, "debounce restarted");
        Assert.True(scheduler.SettingsDirty, "dirty");
        scheduler.Tick(13.5);
        Assert.Equal(1, saves, "saved 2 s after the last change");
        Assert.False(scheduler.SettingsDirty, "clean");
        scheduler.Tick(100);
        Assert.Equal(1, saves, "saved once");
    }

    [Test]
    public void SaveScheduler_ProfileBackgroundCadence()
    {
        var saveTimes = new List<double>();
        double now = 0;
        var scheduler = new SaveScheduler(() => { }, () => saveTimes.Add(now));

        // Learner marks the profile dirty every frame (60 Hz) for 200 s.
        for (int frame = 0; frame <= 200 * 60; frame++)
        {
            now = 5.0 + (frame / 60.0);
            scheduler.MarkProfileDirty(now);
            scheduler.Tick(now);
        }

        Assert.Equal(3, saveTimes.Count, "one save per 60 s");
        Assert.Near(65.0, saveTimes[0], 0.02, "first save 60 s after the first change");
        Assert.Near(125.0, saveTimes[1], 0.05, "second");
        Assert.Near(185.0, saveTimes[2], 0.05, "third");
    }

    [Test]
    public void SaveScheduler_ProfileEditsDebouncedAndPreemptCadence()
    {
        var saveTimes = new List<double>();
        double now = 0;
        var scheduler = new SaveScheduler(() => { }, () => saveTimes.Add(now));

        now = 1.0;
        scheduler.MarkProfileDirty(now); // background due at 61
        now = 10.0;
        scheduler.MarkProfileEdited(now);
        now = 11.0;
        scheduler.MarkProfileEdited(now);
        scheduler.Tick(now);
        now = 12.9;
        scheduler.Tick(now);
        Assert.Equal(0, saveTimes.Count, "debouncing edits");
        now = 13.0;
        scheduler.Tick(now);
        Assert.Equal(1, saveTimes.Count, "edit saved 2 s after the last one");
        Assert.False(scheduler.ProfileDirty, "the save covered the background change too");
        now = 61.0;
        scheduler.Tick(now);
        Assert.Equal(1, saveTimes.Count, "no extra background save");
    }

    [Test]
    public void SaveScheduler_FlushAllSavesOnlyDirty()
    {
        int settingsSaves = 0;
        int profileSaves = 0;
        var scheduler = new SaveScheduler(() => settingsSaves++, () => profileSaves++);

        scheduler.FlushAll();
        Assert.Equal(0, settingsSaves + profileSaves, "nothing dirty");

        scheduler.MarkSettingsDirty(1);
        scheduler.MarkProfileDirty(1);
        scheduler.FlushAll();
        Assert.Equal(1, settingsSaves, "settings flushed");
        Assert.Equal(1, profileSaves, "profile flushed");
        Assert.False(scheduler.SettingsDirty || scheduler.ProfileDirty, "clean");

        scheduler.Tick(1000);
        Assert.Equal(1, settingsSaves, "no duplicate settings save after flush");
        Assert.Equal(1, profileSaves, "no duplicate profile save after flush");

        scheduler.MarkProfileEdited(2000);
        scheduler.FlushProfile();
        Assert.Equal(2, profileSaves, "profile-only flush");
        Assert.Equal(1, settingsSaves, "settings untouched");

        scheduler.MarkProfileDirty(3000);
        scheduler.DiscardProfileChanges();
        scheduler.Tick(1e9);
        Assert.Equal(2, profileSaves, "discarded");
    }

    [Test]
    public void SaveScheduler_FailingCallbackIsLoggedAndRetried()
    {
        var log = new RecordingLog();
        int attempts = 0;
        var scheduler = new SaveScheduler(
            () =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new IOException("disk full");
                }
            },
            () => { },
            log);

        scheduler.MarkSettingsDirty(0);
        scheduler.Tick(2.0);
        Assert.Equal(1, attempts, "first attempt");
        Assert.Equal(1, log.Errors.Count, "logged");
        Assert.True(scheduler.SettingsDirty, "still dirty");
        scheduler.Tick(3.0);
        Assert.Equal(1, attempts, "no retry storm");
        scheduler.Tick(2.0 + SaveScheduler.RetryDelaySeconds);
        Assert.Equal(2, attempts, "retried");
        Assert.False(scheduler.SettingsDirty, "clean after success");

        scheduler.Tick(double.NaN);
        Assert.Throws<ArgumentNullException>(() => new SaveScheduler(null, () => { }), "callbacks required");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Sets every public field reachable from <paramref name="target"/> to a deterministic non-default value, so a
    /// round-trip test covers fields that other modules add to <see cref="BalanceLearnedState"/> later.
    /// </summary>
    internal static void FillDeterministically(object target, int depth)
    {
        const int MaxDepth = 4;
        const int ArrayLength = 5;
        int seed = 1;
        foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.IsInitOnly || field.IsLiteral)
            {
                continue;
            }

            seed++;
            object value = CreateValue(field.FieldType, seed, depth, MaxDepth, ArrayLength);
            if (value != null)
            {
                field.SetValue(target, value);
            }
        }
    }

    private static object CreateValue(Type type, int seed, int depth, int maxDepth, int arrayLength)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(double))
        {
            return (seed * 0.3712) + 0.0001;
        }

        if (underlying == typeof(float))
        {
            return (float)(seed * 0.25);
        }

        if (underlying == typeof(int))
        {
            return seed * 3;
        }

        if (underlying == typeof(long))
        {
            return (seed * 1000003L) + 7;
        }

        if (underlying == typeof(uint))
        {
            return (uint)(seed * 11);
        }

        if (underlying == typeof(ulong))
        {
            return (ulong)(seed * 13);
        }

        if (underlying == typeof(short))
        {
            return (short)seed;
        }

        if (underlying == typeof(byte))
        {
            return (byte)seed;
        }

        if (underlying == typeof(bool))
        {
            return seed % 2 == 0;
        }

        if (underlying == typeof(string))
        {
            return "value-" + seed.ToString(CultureInfo.InvariantCulture);
        }

        if (underlying == typeof(DateTime))
        {
            return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(seed);
        }

        if (underlying.IsEnum)
        {
            Array values = Enum.GetValues(underlying);
            return values.GetValue(seed % values.Length);
        }

        if (depth >= maxDepth)
        {
            return null;
        }

        if (underlying.IsArray && underlying.GetArrayRank() == 1)
        {
            Type elementType = underlying.GetElementType();
            Array array = Array.CreateInstance(elementType, arrayLength);
            for (int i = 0; i < arrayLength; i++)
            {
                array.SetValue(CreateValue(elementType, seed + i, depth + 1, maxDepth, arrayLength), i);
            }

            return array;
        }

        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
        {
            Type elementType = underlying.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(underlying);
            for (int i = 0; i < arrayLength; i++)
            {
                list.Add(CreateValue(elementType, seed + i, depth + 1, maxDepth, arrayLength));
            }

            return list;
        }

        if (underlying.IsClass && underlying.GetConstructor(Type.EmptyTypes) != null)
        {
            object nested = Activator.CreateInstance(underlying);
            FillDeterministically(nested, depth + 1);
            return nested;
        }

        return null;
    }

    /// <summary>Unique temporary directory, deleted on dispose.</summary>
    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SlipLockTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a late thread-pool write may still hold a handle; the OS temp cleanup removes it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>ILog that records messages per level.</summary>
    private sealed class RecordingLog : ILog
    {
        public List<string> Infos { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public List<string> Errors { get; } = new List<string>();

        public void Info(string message)
        {
            lock (Infos)
            {
                Infos.Add(message);
            }
        }

        public void Warn(string message)
        {
            lock (Warnings)
            {
                Warnings.Add(message);
            }
        }

        public void Error(string message)
        {
            lock (Errors)
            {
                Errors.Add(message);
            }
        }
    }

    /// <summary>JSON convention probe.</summary>
    internal sealed class SampleDto
    {
        public double NotANumber;
        public double Infinite;
        public string Text;
        public BalanceMode Mode;
        public List<int> Numbers = new List<int> { 1, 2 };
        public double[] Values;
    }
}
