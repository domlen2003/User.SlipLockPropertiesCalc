using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.SpeedDial.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DivebombLogistics.SpeedDial.Persistence;

/// <summary>
/// Every file of the Speed Dial module below its data directory (<c>PluginsData\DLP\SpeedDial</c>):
/// <list type="bullet">
/// <item>Global settings <c>Settings.json</c> (<see cref="SpeedDialSettings"/>): a missing file gives defaults, a corrupt
/// one is quarantined (<c>.bad-&lt;timestamp&gt;</c>) and replaced by defaults, a file that exists but cannot be read
/// (locked) continues from <c>Settings.unsaved.json</c> (or defaults) and that session's saves go there, so an unread
/// file is never overwritten (and an unreadable side file is never overwritten either, see
/// <see cref="UnsavedSideFile"/>). Written by <see cref="AsyncJsonWriter{T}"/> (asynchronously after the module's debounce,
/// synchronously at shutdown).</item>
/// <item>Per-car data <c>Cars\&lt;Sim&gt;\&lt;CarKey&gt;_&lt;fnv1a8&gt;.json</c> (<see cref="SpeedDialCarData"/>, names from
/// <see cref="CarFileNaming"/> like Haptics), with the haptics car profile rules: atomic writes, corrupt files
/// quarantined and replaced by new data, unreadable files retried briefly and then left untouched (that car continues
/// from and saves to <c>&lt;name&gt;.unsaved.json</c>, see <see cref="UnsavedSideFile"/>), failed asynchronous writes retried with a back-off, content still queued
/// returned by <see cref="LoadCarData"/> instead of the older file.</item>
/// <item>Export/import of a car's data as a user-chosen JSON file (import takes only the presets, see
/// <see cref="AppendPresets"/>).</item>
/// </list>
/// Every loaded object is <c>Normalize()</c>d.
/// <para>
/// Threading: settings and car data are loaded and saved from the data thread, which owns the live objects; the
/// asynchronous saves copy (settings) or serialize (car data) on the calling thread, so the thread pool never touches a
/// live object. <see cref="Export"/> and <see cref="Import"/> do file IO on the calling (UI) thread and touch no live
/// object. Never throws (except for an empty data directory in the constructor).
/// </para>
/// </summary>
internal sealed class SpeedDialStore
{
    /// <summary>File name of the global settings inside the data directory.</summary>
    public const string SettingsFileName = "Settings.json";

    /// <summary>Side file of the settings used while <see cref="SettingsFileName"/> exists but could not be read.</summary>
    public const string UnsavedSettingsFileName = "Settings.unsaved.json";

    /// <summary>Suffix (replacing the extension) of a car's side file used while its real file could not be read.</summary>
    public const string UnsavedSuffix = ".unsaved" + CarFileNaming.FileExtension;

    /// <summary>Import rejects files larger than this; a car's Speed Dial data is a few KB.</summary>
    private const long MaxImportBytes = 16L * 1024 * 1024;

    /// <summary>A transient read error at car load is retried this many times ...</summary>
    private const int ReadRetries = 3;

    /// <summary>... this far apart (car load is a rare path; a short stall is acceptable there).</summary>
    private const int ReadRetryDelayMs = 20;

    /// <summary>What the car files contain, for log messages.</summary>
    private const string CarDataDescription = "Speed Dial car data";

    /// <summary>Members every file written by this store contains; used to recognize foreign JSON on import.</summary>
    private static readonly string[] RequiredImportMembers =
    {
        nameof(SpeedDialCarData.SchemaVersion),
        nameof(SpeedDialCarData.Presets),
    };

    private readonly ILog log;
    private readonly Func<DateTime> utcNow;
    private readonly OrderedFileWriter carWriter;

    /// <summary>Guards <see cref="unreadablePaths"/>.</summary>
    private readonly object unreadableLock = new object();

    /// <summary>Car files that exist but could not be read at load, mapped to the side file this session saves them to.</summary>
    private readonly Dictionary<string, string> unreadablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private AsyncJsonWriter<SpeedDialSettings> settingsWriter;

    /// <summary>Creates a store rooted at the module's data directory.</summary>
    /// <param name="dataDirectory">E.g. <c>&lt;SimHub&gt;\PluginsData\DLP\SpeedDial</c> (created on the first write).</param>
    /// <param name="log">Receives load/save problems.</param>
    public SpeedDialStore(string dataDirectory, ILog log)
        : this(dataDirectory, log, null, null)
    {
    }

    /// <summary>Test constructor: injectable clock (save stamps, quarantine names) and retry delays of failed asynchronous car writes.</summary>
    internal SpeedDialStore(string dataDirectory, ILog log, Func<DateTime> utcNow, int[] writeRetryDelaysMs)
    {
        if (string.IsNullOrEmpty(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }

        DataDirectory = dataDirectory;
        this.log = log ?? NullLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        carWriter = new OrderedFileWriter(CarDataDescription, this.log, writeRetryDelaysMs);
        SettingsSavePath = SettingsPath;
    }

    /// <summary>The module's data directory.</summary>
    public string DataDirectory { get; }

    /// <summary><c>&lt;data directory&gt;\Settings.json</c>.</summary>
    public string SettingsPath => Path.Combine(DataDirectory, SettingsFileName);

    /// <summary>Where this session saves the settings: <see cref="SettingsPath"/>, or the side file after a read error.</summary>
    public string SettingsSavePath { get; private set; }

    // =====================================================================================================
    // Global settings
    // =====================================================================================================

    /// <summary>
    /// Reads and normalizes the settings (call once, at module start). Missing gives defaults; corrupt is quarantined
    /// and gives defaults; unreadable gives defaults and redirects this session's saves to
    /// <see cref="UnsavedSettingsFileName"/>. Never throws.
    /// </summary>
    /// <param name="status">What was found on disk.</param>
    public SpeedDialSettings LoadSettings(out JsonReadStatus status)
    {
        string path = SettingsPath;
        string savePath = path;
        status = JsonFile.TryRead(path, out SpeedDialSettings settings, out int skipped, out string error);
        switch (status)
        {
            case JsonReadStatus.Loaded:
                if (skipped > 0)
                {
                    log.Warn("Settings " + path + ": " + skipped.ToString(CultureInfo.InvariantCulture) + " unreadable value(s) reset to defaults.");
                }

                break;

            case JsonReadStatus.Corrupt:
                string quarantined = JsonFile.Quarantine(path, utcNow().ToLocalTime());
                log.Warn("Settings " + path + " are corrupt (" + error + "); "
                    + (quarantined != null ? "moved to " + quarantined : "could not move them aside") + ", starting with defaults.");
                settings = null;
                break;

            case JsonReadStatus.IoError:
                settings = UnsavedSideFile.Resume<SpeedDialSettings>(
                    Path.Combine(DataDirectory, UnsavedSettingsFileName), utcNow().ToLocalTime(), log, "Speed Dial settings", out savePath);
                log.Error("Could not read settings " + path + " (" + error + "); using "
                    + (settings != null ? "the unsaved side file" : "defaults") + " for this session. "
                    + "The file is left untouched; this session's changes are saved to " + savePath + ".");
                break;
        }

        if (status != JsonReadStatus.IoError)
        {
            UnsavedSideFile.WarnIfLeftOver(Path.Combine(DataDirectory, UnsavedSettingsFileName), log, "Speed Dial settings");
        }

        settings ??= new SpeedDialSettings();
        settings.Normalize();
        SettingsSavePath = savePath;
        settingsWriter = new AsyncJsonWriter<SpeedDialSettings>(savePath, log);
        return settings;
    }

    /// <summary>Copies <paramref name="settings"/> now and writes the copy on the thread pool (data thread). Never throws.</summary>
    public void SaveSettingsAsync(SpeedDialSettings settings) => GetSettingsWriter().SaveAsync(settings);

    /// <summary>Writes the settings synchronously (shutdown), superseding a queued asynchronous save. Never throws.</summary>
    /// <returns>True when written.</returns>
    public bool SaveSettings(SpeedDialSettings settings) => GetSettingsWriter().Save(settings);

    // =====================================================================================================
    // Per-car data
    // =====================================================================================================

    /// <summary>
    /// File of a car's data: <c>&lt;data directory&gt;\Cars\&lt;Sim&gt;\&lt;CarKey&gt;_&lt;fnv1a8&gt;.json</c>
    /// (<see cref="CarFileNaming.GetCarFilePath"/>, the same names Haptics uses).
    /// </summary>
    public string GetCarFilePath(string simKey, string carKey) => CarFileNaming.GetCarFilePath(DataDirectory, simKey, carKey);

    /// <summary>True when the car has stored data: a file on disk or content queued for it by <see cref="SaveCarDataAsync"/>.</summary>
    public bool CarDataExists(string simKey, string carKey)
    {
        string path = SafeGetCarFilePath(simKey, carKey);
        if (path == null)
        {
            return false;
        }

        return carWriter.GetPending(path) != null || File.Exists(path);
    }

    /// <summary>
    /// The stored data of a car (normalized), or new data if none exists or the file is corrupt (then quarantined) or
    /// unreadable. Never null, never throws. The identity fields are always set to the given keys;
    /// <paramref name="displayName"/> replaces the stored name when not empty. New data is not written until saved.
    /// Content still queued by <see cref="SaveCarDataAsync"/> is returned instead of the (older) file content.
    /// </summary>
    public SpeedDialCarData LoadCarData(string simKey, string carKey, string displayName)
    {
        string path = SafeGetCarFilePath(simKey, carKey);
        SpeedDialCarData data = path == null ? null : ReadExisting(path, carKey);
        data ??= new SpeedDialCarData();
        data.Normalize();
        data.SimKey = simKey ?? string.Empty;
        data.CarKey = carKey ?? string.Empty;
        if (!string.IsNullOrEmpty(displayName))
        {
            data.DisplayName = displayName;
        }
        else if (data.DisplayName.Length == 0)
        {
            data.DisplayName = data.CarKey;
        }

        return data;
    }

    /// <summary>
    /// Serializes the data now (on the calling thread, which owns it) and writes it on the thread pool. Several calls
    /// before the write happens coalesce into one write of the newest content. Stamps <see cref="SpeedDialCarData.LastUpdatedUtc"/>.
    /// Never throws.
    /// </summary>
    public void SaveCarDataAsync(SpeedDialCarData data)
    {
        if (TryPrepare(data, out string path, out string json))
        {
            carWriter.WriteAsync(path, json);
        }
    }

    /// <summary>Writes the data synchronously (shutdown), superseding older content still queued for the file. Never throws.</summary>
    /// <returns>True if the file was written.</returns>
    public bool SaveCarData(SpeedDialCarData data) =>
        TryPrepare(data, out string path, out string json) && carWriter.Write(path, json);

    /// <summary>Blocks until queued settings and car writes have finished or the timeout elapsed; true when nothing is pending.</summary>
    public bool WaitForPendingWrites(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        bool carsDone = carWriter.WaitForPendingWrites(timeout);
        TimeSpan remaining = deadline - DateTime.UtcNow;
        bool settingsDone = settingsWriter == null
            || settingsWriter.WaitForPendingWrites(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        return carsDone && settingsDone;
    }

    /// <summary>Side file that receives the saves of a session whose car file could not be read.</summary>
    internal static string UnsavedPath(string path) =>
        path.EndsWith(CarFileNaming.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(0, path.Length - CarFileNaming.FileExtension.Length) + UnsavedSuffix
            : path + UnsavedSuffix;

    // =====================================================================================================
    // Export / import (UI thread)
    // =====================================================================================================

    /// <summary>Writes <paramref name="data"/> (a copy the caller owns) to a user-chosen file. Never throws.</summary>
    /// <param name="data">The car data to export.</param>
    /// <param name="filePath">Destination (overwritten).</param>
    /// <param name="error">Failure description for the UI; null on success.</param>
    public bool Export(SpeedDialCarData data, string filePath, out string error)
    {
        error = null;
        if (data == null)
        {
            error = "no car loaded.";
            return false;
        }

        if (string.IsNullOrEmpty(filePath))
        {
            error = "no file selected.";
            return false;
        }

        try
        {
            JsonFile.WriteAllTextAtomic(filePath, JsonFile.Serialize(data));
            log.Info("Exported Speed Dial data of '" + data.CarKey + "' to " + filePath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Warn("Speed Dial export to " + filePath + " failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Reads a file written by <see cref="Export"/> (or copied from the Cars folder) and normalizes it. Foreign JSON
    /// (e.g. a haptics car profile) is rejected. Never throws.
    /// </summary>
    /// <param name="filePath">File to import.</param>
    /// <param name="error">Failure description for the UI; null on success.</param>
    /// <returns>The file's data, or null on failure.</returns>
    public SpeedDialCarData Import(string filePath, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(filePath))
        {
            error = "no file selected.";
            return null;
        }

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                error = "file not found.";
                return null;
            }

            if (info.Length > MaxImportBytes)
            {
                error = "the file is too large to be Speed Dial data.";
                return null;
            }

            JObject root = JsonFile.ParseObject(File.ReadAllText(filePath, Encoding.UTF8));
            foreach (string member in RequiredImportMembers)
            {
                if (root[member] == null)
                {
                    error = "not a DLP Speed Dial file (missing '" + member + "').";
                    return null;
                }
            }

            SpeedDialCarData data = JsonFile.ToObject<SpeedDialCarData>(root, out int skipped);
            if (skipped > 0)
            {
                log.Warn("Import of " + filePath + " skipped " + skipped.ToString(CultureInfo.InvariantCulture) + " unreadable value(s).");
            }

            data.Normalize();
            log.Info("Read Speed Dial data of '" + data.CarKey + "' from " + filePath + " ("
                + data.Presets.Count.ToString(CultureInfo.InvariantCulture) + " presets)");
            return data;
        }
        catch (Exception ex)
        {
            error = ex is JsonException ? "not a valid Speed Dial file: " + ex.Message : ex.Message;
            log.Warn("Speed Dial import from " + filePath + " failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Appends copies of <paramref name="source"/>'s presets to <paramref name="target"/> (import): ids that already
    /// exist in the target get fresh ones, at most <see cref="SpeedDialCarData.MaxPresets"/> presets in total. Slots,
    /// selection, pair values and learning of the source are ignored.
    /// </summary>
    /// <param name="target">The current car's data (normalized).</param>
    /// <param name="source">Imported data (normalized).</param>
    /// <param name="skipped">Presets left out because the target is full.</param>
    /// <returns>The number of presets added.</returns>
    public static int AppendPresets(SpeedDialCarData target, SpeedDialCarData source, out int skipped)
    {
        skipped = 0;
        if (target == null || source?.Presets == null)
        {
            return 0;
        }

        target.Presets ??= new List<DialPreset>();
        int added = 0;
        foreach (DialPreset preset in source.Presets)
        {
            if (preset == null)
            {
                continue;
            }

            if (target.Presets.Count >= SpeedDialCarData.MaxPresets)
            {
                skipped++;
                continue;
            }

            DialPreset copy = preset.DeepCopy();
            copy.Normalize(target.Presets.Count);
            while (target.IndexOfPreset(copy.Id) >= 0)
            {
                copy.Id = DialPreset.NewId();
            }

            target.Presets.Add(copy);
            added++;
        }

        return added;
    }

    // =====================================================================================================
    // Internals
    // =====================================================================================================

    private AsyncJsonWriter<SpeedDialSettings> GetSettingsWriter() =>
        settingsWriter ??= new AsyncJsonWriter<SpeedDialSettings>(SettingsSavePath, log);

    private string SafeGetCarFilePath(string simKey, string carKey)
    {
        try
        {
            return GetCarFilePath(simKey, carKey);
        }
        catch (Exception ex)
        {
            // Only reachable with an unusable data directory; sanitization removes everything else.
            log.Error("Invalid Speed Dial car file path for '" + carKey + "': " + ex.Message);
            return null;
        }
    }

    /// <summary>Where saves of <paramref name="path"/> go this session: the file itself, or its side file while it is unreadable.</summary>
    private string GetSavePath(string path)
    {
        lock (unreadableLock)
        {
            return unreadablePaths.TryGetValue(path, out string side) ? side : path;
        }
    }

    private SpeedDialCarData ReadExisting(string path, string carKey)
    {
        SpeedDialCarData pending = ReadPending(GetSavePath(path), carKey);
        if (pending != null)
        {
            return pending;
        }

        JsonReadStatus status;
        SpeedDialCarData data;
        int skipped;
        string error;

        // Holding the file lock lets a write in progress finish first (no read of a file being replaced).
        lock (carWriter.GetFileLock(path))
        {
            status = JsonFile.TryRead(path, out data, out skipped, out error);
            for (int attempt = 1; attempt <= ReadRetries && status == JsonReadStatus.IoError; attempt++)
            {
                Thread.Sleep(ReadRetryDelayMs);
                status = JsonFile.TryRead(path, out data, out skipped, out error);
            }
        }

        string sidePath = null;
        if (status == JsonReadStatus.IoError)
        {
            // Continue from this car's side file (an earlier session's, or this session's after a car change).
            string knownSide = GetSavePath(path);
            data = UnsavedSideFile.Resume<SpeedDialCarData>(
                ReferenceEquals(knownSide, path) ? UnsavedPath(path) : knownSide, utcNow().ToLocalTime(), log, CarDataDescription, out sidePath);
        }

        lock (unreadableLock)
        {
            if (status == JsonReadStatus.IoError)
            {
                unreadablePaths[path] = sidePath;
            }
            else
            {
                unreadablePaths.Remove(path);
            }
        }

        switch (status)
        {
            case JsonReadStatus.Loaded:
                if (skipped > 0)
                {
                    log.Warn("Speed Dial car data " + path + ": " + skipped.ToString(CultureInfo.InvariantCulture) + " unreadable value(s) reset to defaults.");
                }

                WarnIfStoredForOtherKey(path, data, carKey);
                return data;

            case JsonReadStatus.Corrupt:
                string quarantined = JsonFile.Quarantine(path, utcNow().ToLocalTime());
                log.Warn("Speed Dial car data " + path + " is corrupt (" + error + "); "
                    + (quarantined != null ? "moved to " + quarantined : "could not move it aside") + ", starting with new data.");
                return null;

            case JsonReadStatus.IoError:
                log.Error("Could not read Speed Dial car data " + path + " (" + error + "); "
                    + (data != null ? "continuing from the unsaved side file" : "starting with new data") + " for this session. "
                    + "The file is left untouched; this session's changes are saved to " + sidePath + ".");
                return data;

            default:
                return null;
        }
    }

    /// <summary>The newest content queued for <paramref name="savePath"/> (not yet on disk), or null.</summary>
    private SpeedDialCarData ReadPending(string savePath, string carKey)
    {
        string json = carWriter.GetPending(savePath);
        if (json == null)
        {
            return null;
        }

        try
        {
            SpeedDialCarData data = JsonFile.Deserialize<SpeedDialCarData>(json, out _);
            WarnIfStoredForOtherKey(savePath, data, carKey);
            return data;
        }
        catch (Exception ex)
        {
            // Cannot happen for content this store serialized; fall back to the file.
            log.Warn("Queued Speed Dial car data for " + savePath + " could not be read back: " + ex.Message);
            return null;
        }
    }

    private void WarnIfStoredForOtherKey(string path, SpeedDialCarData data, string carKey)
    {
        if (data != null && !string.IsNullOrEmpty(data.CarKey) && !string.Equals(data.CarKey, carKey, StringComparison.Ordinal))
        {
            log.Warn("Speed Dial car data " + path + " was stored for '" + data.CarKey + "', using it for '" + carKey + "'.");
        }
    }

    /// <summary>Picks the target file (side file while unreadable), stamps the data and serializes it on the calling thread.</summary>
    private bool TryPrepare(SpeedDialCarData data, out string path, out string json)
    {
        path = null;
        json = null;
        if (data == null)
        {
            return false;
        }

        try
        {
            path = GetSavePath(GetCarFilePath(data.SimKey, data.CarKey));
            data.LastUpdatedUtc = utcNow();
            json = JsonFile.Serialize(data);
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Could not serialize Speed Dial car data '" + data.CarKey + "': " + ex.Message);
            return false;
        }
    }
}
