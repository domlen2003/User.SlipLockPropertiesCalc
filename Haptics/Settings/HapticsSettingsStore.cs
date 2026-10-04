using System;
using System.Globalization;
using System.IO;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;

namespace DivebombLogistics.Haptics.Settings;

/// <summary>
/// Loads the global <see cref="HapticsSettings"/> from <c>&lt;module data&gt;\Settings.json</c> with the same
/// robustness rules as the car profiles: a missing file gives defaults; a corrupt file is quarantined
/// (<c>.bad-&lt;timestamp&gt;</c>) and replaced by defaults; a file that exists but cannot be read (locked) gives
/// the content of <c>Settings.unsaved.json</c> (or defaults) for this session, and that session's saves go there so the
/// unread file is never overwritten with defaults (nor an unreadable side file, see <see cref="UnsavedSideFile"/>). Never throws. Saving is done by <see cref="AsyncJsonWriter{T}"/> at <see cref="LoadResult.SavePath"/>.
/// </summary>
internal static class HapticsSettingsStore
{
    /// <summary>File name inside the haptics data directory.</summary>
    public const string FileName = "Settings.json";

    /// <summary>Side file used while <see cref="FileName"/> exists but could not be read.</summary>
    public const string UnsavedFileName = "Settings.unsaved.json";

    /// <summary>Path of the settings file inside <paramref name="dataDirectory"/>.</summary>
    public static string GetPath(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>Reads and normalizes the settings of <paramref name="dataDirectory"/>.</summary>
    /// <param name="dataDirectory">The haptics data directory (<c>PluginsData\DLP\Haptics</c>).</param>
    /// <param name="log">Receives what happened to unreadable files.</param>
    /// <param name="localNow">Clock for quarantine names; defaults to <see cref="DateTime.Now"/>.</param>
    public static LoadResult Load(string dataDirectory, ILog log, Func<DateTime> localNow = null)
    {
        log ??= NullLog.Instance;
        string path = GetPath(dataDirectory);
        string savePath = path;
        JsonReadStatus status = JsonFile.TryRead(path, out HapticsSettings settings, out int skipped, out string error);
        switch (status)
        {
            case JsonReadStatus.Loaded:
                if (skipped > 0)
                {
                    log.Warn("Settings " + path + ": " + skipped.ToString(CultureInfo.InvariantCulture) + " unreadable value(s) reset to defaults.");
                }

                break;

            case JsonReadStatus.Corrupt:
                string quarantined = JsonFile.Quarantine(path, (localNow ?? (() => DateTime.Now))());
                log.Warn("Settings " + path + " are corrupt (" + error + "); "
                    + (quarantined != null ? "moved to " + quarantined : "could not move them aside") + ", starting with defaults.");
                settings = null;
                break;

            case JsonReadStatus.IoError:
                settings = UnsavedSideFile.Resume<HapticsSettings>(
                    Path.Combine(dataDirectory, UnsavedFileName), (localNow ?? (() => DateTime.Now))(), log, "Haptics settings", out savePath);
                log.Error("Could not read settings " + path + " (" + error + "); using "
                    + (settings != null ? "the unsaved side file" : "defaults") + " for this session. "
                    + "The file is left untouched; this session's changes are saved to " + savePath + ".");
                break;
        }

        if (status != JsonReadStatus.IoError)
        {
            UnsavedSideFile.WarnIfLeftOver(Path.Combine(dataDirectory, UnsavedFileName), log, "Haptics settings");
        }

        settings ??= new HapticsSettings();
        settings.Normalize();
        return new LoadResult(settings, status, savePath);
    }

    /// <summary>Outcome of <see cref="Load"/>.</summary>
    internal sealed class LoadResult
    {
        public LoadResult(HapticsSettings settings, JsonReadStatus status, string savePath)
        {
            Settings = settings;
            Status = status;
            SavePath = savePath;
        }

        /// <summary>The settings to use (normalized; defaults unless <see cref="Status"/> is Loaded).</summary>
        public HapticsSettings Settings { get; }

        /// <summary>What was found on disk.</summary>
        public JsonReadStatus Status { get; }

        /// <summary>Where this session saves (the settings file, or the side file after a read error).</summary>
        public string SavePath { get; }
    }
}
