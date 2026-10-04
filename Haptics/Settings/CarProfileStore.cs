using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DivebombLogistics.Haptics.Settings;

/// <summary>
/// Loads and saves <see cref="CarProfile"/> files at <c>&lt;root&gt;\Cars\&lt;sim&gt;\&lt;car&gt;_&lt;hash&gt;.json</c>.
/// <para>
/// Robustness rules (a broken file must never crash SimHub or silently destroy data): all writes are atomic
/// (<see cref="JsonFile.WriteAllTextAtomic"/>), unreadable files are quarantined (<c>.bad-&lt;timestamp&gt;</c>) and
/// replaced by a fresh default profile, and every IO failure is logged and swallowed. A file that exists but cannot
/// be read (locked by an antivirus scan or a sync tool) is retried briefly; if it stays unreadable the car continues
/// from its side file (<c>&lt;name&gt;.unsaved.json</c>, written by an earlier such session) or starts with defaults,
/// and that session's saves go to the side file so defaults never replace data that was never read (an unreadable
/// side file is not overwritten either, see <see cref="UnsavedSideFile"/>). A failed asynchronous write is retried with a back-off.
/// </para>
/// <para>
/// Threading: <see cref="Load"/>, <see cref="Save"/> and <see cref="SaveAsync"/> are called from the data thread,
/// which owns the profile objects. <see cref="SaveAsync"/> serializes on the calling thread (so the thread pool
/// never touches a live profile) and writes on the thread pool. Writes to one path are strictly ordered and never
/// overlap; a newer content always wins over an older one still queued, including across <see cref="Save"/>.
/// </para>
/// </summary>
internal sealed class CarProfileStore
{
    /// <summary>Sub folder of the module data directory holding the car profiles.</summary>
    public const string CarsFolderName = CarFileNaming.CarsFolderName;

    /// <summary>Extension of car profile files.</summary>
    public const string FileExtension = CarFileNaming.FileExtension;

    /// <summary>Import rejects files larger than this; a car profile is well below 1 MB.</summary>
    private const long MaxImportBytes = 16L * 1024 * 1024;

    /// <summary>Suffix (replacing <see cref="FileExtension"/>) of the side file used while the real file is unreadable.</summary>
    public const string UnsavedSuffix = ".unsaved" + FileExtension;

    /// <summary>A transient read error at car load is retried this many times ...</summary>
    private const int ReadRetries = 3;

    /// <summary>... this far apart (car load is a rare path; a short stall is acceptable there).</summary>
    private const int ReadRetryDelayMs = 20;

    /// <summary>Members every profile file written by this plugin contains; used to recognize foreign JSON on import.</summary>
    private static readonly string[] RequiredImportMembers = { nameof(CarProfile.SchemaVersion), nameof(CarProfile.CarKey) };

    /// <summary>Delays (ms) between the attempts of a failed asynchronous write; after the last one it is dropped.</summary>
    private static readonly int[] DefaultWriteRetryDelaysMs = { 250, 1000, 4000, 15000 };

    private readonly ILog _log;
    private readonly Func<DateTime> _utcNow;

    /// <summary>Guards <see cref="_slots"/>, every slot's pending fields and <see cref="_activeWriters"/>.</summary>
    private readonly object _stateLock = new object();
    private readonly Dictionary<string, WriteSlot> _slots = new Dictionary<string, WriteSlot>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Profile files that exist but could not be read at load, mapped to the side file their saves are redirected to (guarded by the state lock).</summary>
    private readonly Dictionary<string, string> _unreadablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly int[] _writeRetryDelaysMs;
    private long _nextSequence;
    private int _activeWriters;

    /// <summary>Creates a store rooted at the module data directory (file names: <see cref="CarFileNaming"/>).</summary>
    /// <param name="rootDirectory">Module data directory, e.g. <c>&lt;SimHub&gt;\PluginsData\DLP\Haptics</c>.</param>
    /// <param name="log">Receives load/save problems.</param>
    public CarProfileStore(string rootDirectory, ILog log)
        : this(rootDirectory, log, () => DateTime.UtcNow)
    {
    }

    /// <summary>Test constructor with an injectable clock (profile time stamps, quarantine names).</summary>
    internal CarProfileStore(string rootDirectory, ILog log, Func<DateTime> utcNow)
        : this(rootDirectory, log, utcNow, DefaultWriteRetryDelaysMs)
    {
    }

    /// <summary>Test constructor with an injectable clock and retry delays of failed asynchronous writes.</summary>
    internal CarProfileStore(string rootDirectory, ILog log, Func<DateTime> utcNow, int[] writeRetryDelaysMs)
    {
        if (string.IsNullOrEmpty(rootDirectory))
        {
            throw new ArgumentException("A root directory is required.", nameof(rootDirectory));
        }

        RootDirectory = rootDirectory;
        _log = log ?? NullLog.Instance;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _writeRetryDelaysMs = writeRetryDelaysMs ?? DefaultWriteRetryDelaysMs;
    }

    /// <summary>Module data directory passed to the constructor.</summary>
    public string RootDirectory { get; }

    /// <summary>
    /// File path of a car's profile. Deterministic: the same keys always map to the same file, and keys that differ
    /// in any character (even ones replaced by sanitization, or only in case) map to different files.
    /// </summary>
    public string GetProfilePath(string simKey, string carKey) => CarFileNaming.GetCarFilePath(RootDirectory, simKey, carKey);

    /// <summary>
    /// Returns the stored profile of a car (normalized) or a new default profile (100 % sensitivities) if none
    /// exists or the file is corrupt (then quarantined). Never throws. The profile's identity fields are always set
    /// to the given keys; <paramref name="displayName"/> and <paramref name="carClass"/> update the stored ones when
    /// not empty. A new profile is not written until it is saved. Content still queued by <see cref="SaveAsync"/> for
    /// the file is returned instead of the (older) file content.
    /// </summary>
    public CarProfile Load(string simKey, string carKey, string displayName, string carClass)
    {
        string path = SafeGetProfilePath(simKey, carKey);
        CarProfile profile = path == null ? null : ReadExisting(path, carKey);
        profile ??= new CarProfile();

        profile.Normalize();
        profile.SimKey = simKey ?? string.Empty;
        profile.CarKey = carKey ?? string.Empty;
        if (!string.IsNullOrEmpty(displayName))
        {
            profile.DisplayName = displayName;
        }
        else if (profile.DisplayName.Length == 0)
        {
            profile.DisplayName = profile.CarKey;
        }

        if (!string.IsNullOrEmpty(carClass))
        {
            profile.CarClass = carClass;
        }

        return profile;
    }

    /// <summary>True when the car has a stored profile: a file on disk or content queued for it by <see cref="SaveAsync"/>.</summary>
    public bool ProfileExists(string simKey, string carKey)
    {
        string path = SafeGetProfilePath(simKey, carKey);
        if (path == null)
        {
            return false;
        }

        lock (_stateLock)
        {
            if (_slots.TryGetValue(path, out WriteSlot slot) && slot.PendingJson != null)
            {
                return true;
            }
        }

        try
        {
            return File.Exists(path);
        }
        catch (Exception ex)
        {
            _log.Warn("Could not check car profile " + path + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Writes the profile synchronously (for SimHub shutdown). Supersedes any older content still queued by
    /// <see cref="SaveAsync"/> for the same file. Never throws.
    /// </summary>
    /// <returns>True if the file was written (or a newer write already superseded this one).</returns>
    public bool Save(CarProfile profile)
    {
        if (!TryPrepare(profile, out string path, out string json))
        {
            return false;
        }

        WriteSlot slot;
        long sequence;
        lock (_stateLock)
        {
            slot = GetSlot(path);
            sequence = ++_nextSequence;

            // Anything queued so far is older than this content: drop it rather than let it overwrite us later.
            slot.PendingJson = null;
        }

        return WriteIfNewest(slot, sequence, json);
    }

    /// <summary>
    /// Serializes the profile now (on the calling thread) and writes it on the thread pool. Several calls for one
    /// file before the write happens coalesce into one write of the newest content. Never throws.
    /// </summary>
    public void SaveAsync(CarProfile profile)
    {
        if (!TryPrepare(profile, out string path, out string json))
        {
            return;
        }

        lock (_stateLock)
        {
            WriteSlot slot = GetSlot(path);
            slot.PendingJson = json;
            slot.PendingSequence = ++_nextSequence;
            if (slot.WriterQueued)
            {
                // The running/queued writer for this file picks the new content up.
                return;
            }

            slot.WriterQueued = true;
            _activeWriters++;
            try
            {
                ThreadPool.QueueUserWorkItem(DrainSlot, slot);
            }
            catch (Exception ex)
            {
                slot.WriterQueued = false;
                _activeWriters--;
                _log.Error("Could not queue car profile save for " + path + ": " + ex.Message);
            }
        }
    }

    /// <summary>Blocks until all queued <see cref="SaveAsync"/> writes have finished or the timeout elapsed.</summary>
    /// <returns>True if nothing is pending anymore.</returns>
    public bool WaitForPendingWrites(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        lock (_stateLock)
        {
            while (_activeWriters > 0)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_stateLock, remaining))
                {
                    return _activeWriters == 0;
                }
            }

            return true;
        }
    }

    /// <summary>Writes a copy of the profile to a user-chosen file. Never throws.</summary>
    /// <param name="profile">Profile to export.</param>
    /// <param name="filePath">Destination file (overwritten).</param>
    /// <param name="error">Failure description for the UI; null on success.</param>
    public bool Export(CarProfile profile, string filePath, out string error)
    {
        error = null;
        if (profile == null)
        {
            error = "No car profile loaded.";
            return false;
        }

        try
        {
            JsonFile.WriteAllTextAtomic(filePath, JsonFile.Serialize(profile));
            _log.Info("Exported car profile '" + profile.CarKey + "' to " + filePath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Warn("Car profile export to " + filePath + " failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Reads a profile exported by <see cref="Export"/> (or copied from the Cars folder). The result is normalized
    /// (sensitivities clamped, null members repaired) but keeps the identity stored in the file: the caller rebinds
    /// <see cref="CarProfile.SimKey"/>/<see cref="CarProfile.CarKey"/>/<see cref="CarProfile.DisplayName"/>/
    /// <see cref="CarProfile.CarClass"/> to the car it imports into. Never throws.
    /// </summary>
    /// <param name="filePath">File to import.</param>
    /// <param name="error">Failure description for the UI; null on success.</param>
    /// <returns>The imported profile, or null on failure.</returns>
    public CarProfile Import(string filePath, out string error)
    {
        error = null;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                error = "File not found.";
                return null;
            }

            if (info.Length > MaxImportBytes)
            {
                error = "File is too large to be a car profile.";
                return null;
            }

            JObject root = JsonFile.ParseObject(File.ReadAllText(filePath, Encoding.UTF8));
            foreach (string member in RequiredImportMembers)
            {
                if (root[member] == null)
                {
                    error = "Not a DLP haptics car profile (missing '" + member + "').";
                    return null;
                }
            }

            CarProfile profile = JsonFile.ToObject<CarProfile>(root, out int skipped);
            if (skipped > 0)
            {
                _log.Warn("Import of " + filePath + " skipped " + skipped.ToString(CultureInfo.InvariantCulture) + " unreadable value(s).");
            }

            profile.Normalize();
            _log.Info("Imported car profile '" + profile.CarKey + "' from " + filePath);
            return profile;
        }
        catch (Exception ex)
        {
            error = ex is JsonException ? "Not a valid profile file: " + ex.Message : ex.Message;
            _log.Warn("Car profile import from " + filePath + " failed: " + ex.Message);
            return null;
        }
    }

    private string SafeGetProfilePath(string simKey, string carKey)
    {
        try
        {
            return GetProfilePath(simKey, carKey);
        }
        catch (Exception ex)
        {
            // Only reachable with an unusable root directory; sanitization removes everything else.
            _log.Error("Invalid car profile path for '" + carKey + "': " + ex.Message);
            return null;
        }
    }

    private CarProfile ReadExisting(string path, string carKey)
    {
        string knownSavePath;
        lock (_stateLock)
        {
            knownSavePath = _unreadablePaths.TryGetValue(path, out string side) ? side : path;
        }

        CarProfile pending = ReadPending(knownSavePath, carKey);
        if (pending != null)
        {
            return pending;
        }

        WriteSlot slot;
        lock (_stateLock)
        {
            slot = GetSlot(path);
        }

        JsonReadStatus status;
        CarProfile profile;
        int skipped;
        string error;

        // Holding the file lock lets a write in progress finish first (no read of a file being replaced).
        lock (slot.FileLock)
        {
            status = JsonFile.TryRead(path, out profile, out skipped, out error);
            for (int attempt = 1; attempt <= ReadRetries && status == JsonReadStatus.IoError; attempt++)
            {
                Thread.Sleep(ReadRetryDelayMs);
                status = JsonFile.TryRead(path, out profile, out skipped, out error);
            }
        }

        string sidePath = null;
        if (status == JsonReadStatus.IoError)
        {
            // Continue from this car's side file (an earlier session's, or this session's after a car change).
            profile = UnsavedSideFile.Resume<CarProfile>(
                ReferenceEquals(knownSavePath, path) ? UnsavedPath(path) : knownSavePath, _utcNow().ToLocalTime(), _log, "Car profile", out sidePath);
        }

        lock (_stateLock)
        {
            if (status == JsonReadStatus.IoError)
            {
                _unreadablePaths[path] = sidePath;
            }
            else
            {
                _unreadablePaths.Remove(path);
            }
        }

        switch (status)
        {
            case JsonReadStatus.Loaded:
                if (skipped > 0)
                {
                    _log.Warn("Car profile " + path + ": " + skipped.ToString(CultureInfo.InvariantCulture) + " unreadable value(s) reset to defaults.");
                }

                WarnIfStoredForOtherKey(path, profile, carKey);
                return profile;

            case JsonReadStatus.Corrupt:
                string quarantined = JsonFile.Quarantine(path, _utcNow().ToLocalTime());
                _log.Warn("Car profile " + path + " is corrupt (" + error + "); "
                    + (quarantined != null ? "moved to " + quarantined : "could not move it aside") + ", starting with defaults.");
                return null;

            case JsonReadStatus.IoError:
                _log.Error("Could not read car profile " + path + " (" + error + "); using "
                    + (profile != null ? "the unsaved side file" : "defaults") + " for this session. "
                    + "The file is left untouched; this session's changes are saved to " + sidePath + ".");
                return profile;

            default:
                return null;
        }
    }

    /// <summary>The newest content queued by <see cref="SaveAsync"/> for the file (not yet on disk), or null.</summary>
    private CarProfile ReadPending(string path, string carKey)
    {
        string json;
        lock (_stateLock)
        {
            json = _slots.TryGetValue(path, out WriteSlot slot) ? slot.PendingJson : null;
        }

        if (json == null)
        {
            return null;
        }

        try
        {
            CarProfile profile = JsonFile.Deserialize<CarProfile>(json, out _);
            WarnIfStoredForOtherKey(path, profile, carKey);
            return profile;
        }
        catch (Exception ex)
        {
            // Cannot happen for content this store serialized; fall back to the file.
            _log.Warn("Queued car profile content for " + path + " could not be read back: " + ex.Message);
            return null;
        }
    }

    private void WarnIfStoredForOtherKey(string path, CarProfile profile, string carKey)
    {
        if (profile != null && !string.IsNullOrEmpty(profile.CarKey) && !string.Equals(profile.CarKey, carKey, StringComparison.Ordinal))
        {
            _log.Warn("Car profile " + path + " was stored for '" + profile.CarKey + "', using it for '" + carKey + "'.");
        }
    }

    /// <summary>Side file that receives the saves of a session whose profile file could not be read.</summary>
    internal static string UnsavedPath(string path) =>
        path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(0, path.Length - FileExtension.Length) + UnsavedSuffix
            : path + UnsavedSuffix;

    /// <summary>Validates the profile, stamps it and serializes it on the calling thread.</summary>
    private bool TryPrepare(CarProfile profile, out string path, out string json)
    {
        path = null;
        json = null;
        if (profile == null)
        {
            return false;
        }

        try
        {
            path = GetProfilePath(profile.SimKey, profile.CarKey);
            lock (_stateLock)
            {
                if (_unreadablePaths.TryGetValue(path, out string side))
                {
                    // The real file exists but was never read: never replace it with this session's defaults.
                    path = side;
                }
            }

            profile.LastUpdatedUtc = _utcNow();
            json = JsonFile.Serialize(profile);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Could not serialize car profile '" + profile.CarKey + "': " + ex.Message);
            return false;
        }
    }

    private WriteSlot GetSlot(string path)
    {
        if (!_slots.TryGetValue(path, out WriteSlot slot))
        {
            slot = new WriteSlot(path);
            _slots[path] = slot;
        }

        return slot;
    }

    /// <summary>
    /// Thread-pool (or retry timer) worker: writes the newest pending content of one file until nothing is pending.
    /// The content stays pending until it is on disk, so <see cref="Load"/> (via <see cref="ReadPending"/>) never misses
    /// it while a write is in progress or waits for a retry and never falls back to the older or unreadable file in
    /// between. A failed write schedules a retry (see <see cref="ScheduleRetry"/>).
    /// </summary>
    private void DrainSlot(object state)
    {
        var slot = (WriteSlot)state;
        try
        {
            while (true)
            {
                string json;
                long sequence;
                lock (_stateLock)
                {
                    json = slot.PendingJson;
                    sequence = slot.PendingSequence;
                    if (json == null)
                    {
                        slot.WriterQueued = false;
                        slot.RetryCount = 0;
                        return;
                    }
                }

                if (WriteIfNewest(slot, sequence, json))
                {
                    lock (_stateLock)
                    {
                        // On disk now: no longer pending, unless newer content arrived meanwhile (written next).
                        if (slot.PendingSequence == sequence)
                        {
                            slot.PendingJson = null;
                        }

                        slot.RetryCount = 0;
                    }
                }
                else if (ScheduleRetry(slot, sequence))
                {
                    return; // the retry timer continues; WriterQueued stays set so new content coalesces
                }
            }
        }
        catch (Exception ex)
        {
            // WriteIfNewest already swallows IO errors; this is the last line of defense for the thread pool,
            // where an unhandled exception would terminate SimHub.
            lock (_stateLock)
            {
                // Pending content always has a writer (or a retry) behind it; without one it would never be written.
                slot.WriterQueued = false;
                slot.PendingJson = null;
            }

            _log.Error("Car profile writer for " + slot.Path + " failed: " + ex.Message);
        }
        finally
        {
            lock (_stateLock)
            {
                _activeWriters--;
                Monitor.PulseAll(_stateLock);
            }
        }
    }

    /// <summary>
    /// After a failed write of <paramref name="sequence"/> (still pending): re-runs the writer after the next back-off
    /// delay. Returns false when the caller should keep draining instead: newer content was queued meanwhile (written
    /// right away), a synchronous <see cref="Save"/> superseded it, or the retries are used up (the content is dropped;
    /// the next save of the profile writes it again).
    /// </summary>
    private bool ScheduleRetry(WriteSlot slot, long sequence)
    {
        lock (_stateLock)
        {
            if (slot.PendingJson == null || slot.PendingSequence != sequence)
            {
                slot.RetryCount = 0;
                return false;
            }

            if (slot.RetryCount >= _writeRetryDelaysMs.Length)
            {
                slot.RetryCount = 0;
                slot.PendingJson = null;
                _log.Error("Giving up saving car profile " + slot.Path + " after "
                    + (_writeRetryDelaysMs.Length + 1).ToString(CultureInfo.InvariantCulture) + " attempts; the next save writes it again.");
                return false;
            }

            int delay = _writeRetryDelaysMs[slot.RetryCount++];
            try
            {
                slot.RetryTimer?.Dispose();
                _activeWriters++; // the timer's DrainSlot run
                slot.RetryTimer = new Timer(DrainSlot, slot, delay, Timeout.Infinite);
                return true;
            }
            catch (Exception ex)
            {
                _activeWriters--;
                slot.PendingJson = null;
                slot.RetryCount = 0;
                _log.Error("Could not schedule a retry for car profile " + slot.Path + ": " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Writes <paramref name="json"/> unless a newer content was written already. Never throws.</summary>
    private bool WriteIfNewest(WriteSlot slot, long sequence, string json)
    {
        lock (slot.FileLock)
        {
            if (sequence <= slot.WrittenSequence)
            {
                return true;
            }

            try
            {
                JsonFile.WriteAllTextAtomic(slot.Path, json);
                slot.WrittenSequence = sequence;
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Could not save car profile " + slot.Path + ": " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Write bookkeeping of one profile file.</summary>
    private sealed class WriteSlot
    {
        public WriteSlot(string path)
        {
            Path = path;
        }

        public string Path { get; }

        /// <summary>Held while the file is written; serializes writers of this path.</summary>
        public object FileLock { get; } = new object();

        /// <summary>
        /// Newest content not on disk yet: queued, being written or waiting for a retry (guarded by the store's state
        /// lock). Cleared only after it was written, superseded or given up.
        /// </summary>
        public string PendingJson;

        public long PendingSequence;

        /// <summary>A thread-pool writer is queued or running for this path (guarded by the state lock).</summary>
        public bool WriterQueued;

        /// <summary>Sequence of the content currently on disk (guarded by <see cref="FileLock"/>).</summary>
        public long WrittenSequence;

        /// <summary>Failed attempts of the pending content so far (guarded by the state lock).</summary>
        public int RetryCount;

        /// <summary>Back-off timer of the next attempt; kept referenced so it is not collected (guarded by the state lock).</summary>
        public Timer RetryTimer;
    }
}
