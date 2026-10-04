using System;
using System.Globalization;
using System.IO;

namespace DivebombLogistics.Core.Persistence;

/// <summary>
/// The side file (<c>&lt;name&gt;.unsaved.json</c>) that receives a session's saves while the real file exists but
/// cannot be read (locked by cloud sync or a virus scanner). A later session whose real file is still unreadable
/// continues from that side file instead of starting from defaults and overwriting it. When the side file cannot be
/// read either (or a corrupt one cannot be moved aside), that session saves to a timestamped side file
/// (<c>&lt;name&gt;.unsaved-&lt;yyyyMMdd_HHmmss&gt;.json</c>), so an earlier session's changes are never replaced by a
/// defaults-only session. Side files are never merged back automatically; a left-over one is reported in the log.
/// Pure (file IO only, no SimHub); never throws.
/// </summary>
internal static class UnsavedSideFile
{
    private const string TimestampFormat = "yyyyMMdd_HHmmss";
    private const string JsonExtension = ".json";

    /// <summary>Upper bound for timestamped name collisions within one second.</summary>
    private const int MaxNameAttempts = 100;

    /// <summary>
    /// The real file was unreadable: reads <paramref name="sidePath"/> to continue from it. Missing gives null
    /// (start fresh, save to the side file); corrupt is quarantined and gives null; unreadable gives null and a
    /// timestamped save path.
    /// </summary>
    /// <param name="sidePath">The side file of the unreadable file.</param>
    /// <param name="localNow">Local time for quarantine and timestamped names.</param>
    /// <param name="log">Receives what happened.</param>
    /// <param name="description">What the file holds, for log messages (e.g. "Settings").</param>
    /// <param name="savePath">Where this session saves: <paramref name="sidePath"/> or a timestamped side file.</param>
    /// <returns>The side file's content, or null to start fresh.</returns>
    public static T Resume<T>(string sidePath, DateTime localNow, ILog log, string description, out string savePath)
        where T : class
    {
        log ??= NullLog.Instance;
        savePath = sidePath;
        JsonReadStatus status = JsonFile.TryRead(sidePath, out T value, out _, out string error);
        switch (status)
        {
            case JsonReadStatus.Loaded:
                log.Warn(description + ": continuing from the unsaved side file " + sidePath
                    + " of an earlier session; this session saves there again.");
                return value;

            case JsonReadStatus.Corrupt:
                string quarantined = JsonFile.Quarantine(sidePath, localNow);
                if (quarantined == null)
                {
                    savePath = TimestampedPath(sidePath, localNow);
                }

                log.Warn(description + ": the side file " + sidePath + " is corrupt (" + error + "); "
                    + (quarantined != null ? "moved to " + quarantined : "could not move it aside") + ".");
                return null;

            case JsonReadStatus.IoError:
                savePath = TimestampedPath(sidePath, localNow);
                log.Warn(description + ": the side file " + sidePath + " cannot be read either (" + error
                    + "); it is kept, and this session's changes are saved to " + savePath + ".");
                return null;

            default:
                return null;
        }
    }

    /// <summary>Logs a warning when <paramref name="sidePath"/> exists although the real file was read (its changes are not merged).</summary>
    public static void WarnIfLeftOver(string sidePath, ILog log, string description)
    {
        try
        {
            if (File.Exists(sidePath))
            {
                (log ?? NullLog.Instance).Warn(description + ": an unsaved side file from an earlier session exists (" + sidePath
                    + "); its changes were not merged. Copy what you need from it, then delete it.");
            }
        }
        catch (Exception ex) when (JsonFile.IsIoException(ex))
        {
            // Only a hint; nothing to do.
        }
    }

    /// <summary><c>&lt;name&gt;.unsaved-&lt;yyyyMMdd_HHmmss&gt;[-n].json</c> next to <paramref name="sidePath"/>, not yet existing.</summary>
    public static string TimestampedPath(string sidePath, DateTime localNow)
    {
        string stem = sidePath.EndsWith(JsonExtension, StringComparison.OrdinalIgnoreCase)
            ? sidePath.Substring(0, sidePath.Length - JsonExtension.Length)
            : sidePath;
        string baseName = stem + "-" + localNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string candidate = baseName + JsonExtension;
        try
        {
            for (int attempt = 1; File.Exists(candidate) && attempt < MaxNameAttempts; attempt++)
            {
                candidate = baseName + "-" + attempt.ToString(CultureInfo.InvariantCulture) + JsonExtension;
            }
        }
        catch (Exception ex) when (JsonFile.IsIoException(ex))
        {
            // Keep the first candidate.
        }

        return candidate;
    }
}
