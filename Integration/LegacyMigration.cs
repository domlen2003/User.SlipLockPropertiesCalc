using System;
using System.Globalization;
using System.IO;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.Framework;
using DivebombLogistics.Haptics;
using DivebombLogistics.Haptics.Settings;
using Newtonsoft.Json;

namespace DivebombLogistics.Integration;

/// <summary>
/// One-time migration of the data of the v1/v2 plugin "Slip Lock Properties Calc" (class
/// <c>User.SlipLockPropertiesCalc.SlipLockPropertiesCalc</c>) into the DLP folders. Runs in <c>DLP.Init</c> before the
/// modules load:
/// <list type="bullet">
/// <item>Settings: when <c>PluginsData\DLP\Haptics\Settings.json</c> does not exist and
/// <c>PluginsData\Common\SlipLockPropertiesCalc.GeneralSettings.json</c> does, the old file is deserialized like SimHub
/// did (Newtonsoft defaults), normalized and written to the new path.</item>
/// <item>Car profiles: when <c>PluginsData\DLP\Haptics\Cars\</c> does not exist and
/// <c>PluginsData\SlipLockPropertiesCalc\Cars\</c> does, the tree is copied (files only; temporary, quarantined
/// <c>.bad-*</c> and <c>.unsaved.json</c> side files are skipped). The copy goes to a staging folder first and is
/// renamed at the end, so an interrupted copy is redone completely on the next start.</item>
/// </list>
/// The old files are never modified or deleted (they are the user's backup). Afterwards the marker
/// <c>PluginsData\DLP\migration.json</c> is written and later starts skip the migration.
/// <para>
/// Failures (IO or unexpected) are logged and swallowed per part, so a failing settings part never skips the car
/// profiles. The plugin then starts with defaults and, in that session, writes its own <c>Settings.json</c> and car
/// profiles. So that the retry still happens, a failed run writes <c>migration-pending.json</c>
/// (<see cref="MigrationPending"/>) instead of the marker, and the next start redoes exactly the failed parts even
/// though their targets exist by then: the old settings replace the defaults written meanwhile (the replaced file is
/// kept as <c>Settings.json.pre-migration</c>), and the old car tree is merged file by file without overwriting any
/// profile DLP has written since. Pure file logic (paths in), so it is unit-tested with a temporary folder.
/// </para>
/// </summary>
internal static class LegacyMigration
{
    /// <summary>Marker file inside <c>PluginsData\DLP</c>.</summary>
    public const string MarkerFileName = "migration.json";

    /// <summary>Record of the failed parts inside <c>PluginsData\DLP</c>; exists only while a retry is pending.</summary>
    public const string PendingFileName = "migration-pending.json";

    /// <summary>The v1/v2 settings file inside <c>PluginsData\Common</c> (SimHub common settings, key "GeneralSettings").</summary>
    public const string LegacySettingsFileName = "SlipLockPropertiesCalc.GeneralSettings.json";

    /// <summary>The v1/v2 data folder inside <c>PluginsData</c>.</summary>
    public const string LegacyDataFolderName = "SlipLockPropertiesCalc";

    /// <summary>SimHub's folder for common plugin settings inside <c>PluginsData</c>.</summary>
    public const string CommonFolderName = "Common";

    /// <summary>Suffix of the staging folder the car profiles are copied into before the final rename.</summary>
    public const string StagingSuffix = ".migrating";

    /// <summary>Suffix of the copy of a DLP <c>Settings.json</c> that a retried settings migration replaced.</summary>
    public const string PreMigrationSuffix = ".pre-migration";

    private const string TempSuffix = ".tmp";
    private const string UnsavedSuffix = ".unsaved.json";
    private const string QuarantineInfix = ".bad-";
    private const string ProfileExtension = ".json";
    private const string TimestampFormat = "o";

    /// <summary>Migrates what is missing (or failed at the last start) and writes the marker. Never throws.</summary>
    /// <param name="pluginsDataDirectory"><c>&lt;SimHub&gt;\PluginsData</c>.</param>
    /// <param name="log">Receives what was migrated and every failure.</param>
    /// <param name="utcNow">Clock for the marker; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public static MigrationResult Run(string pluginsDataDirectory, ILog log, Func<DateTime> utcNow = null)
    {
        log ??= NullLog.Instance;
        var result = new MigrationResult();
        try
        {
            string dlpRoot = Path.Combine(pluginsDataDirectory, DlpNames.DataFolderName);
            string markerPath = Path.Combine(dlpRoot, MarkerFileName);
            string pendingPath = Path.Combine(dlpRoot, PendingFileName);
            if (File.Exists(markerPath))
            {
                result.AlreadyMigrated = true;
                return result;
            }

            MigrationPending pending = ReadPending(pendingPath, log);
            result.Retry = pending != null;

            string hapticsDirectory = Path.Combine(dlpRoot, HapticsModule.ModuleId);
            MigrateSettings(pluginsDataDirectory, hapticsDirectory, pending != null && pending.SettingsFailed, log, result);
            MigrateCarProfiles(pluginsDataDirectory, hapticsDirectory, pending != null && pending.CarProfilesFailed, log, result);

            // Totals over this and earlier incomplete runs.
            bool settingsMigrated = result.SettingsMigrated || (pending != null && pending.SettingsMigrated);
            int carProfiles = result.CarProfiles + (pending != null ? Math.Max(0, pending.CarProfiles) : 0);
            if (result.SettingsFailed || result.CarProfilesFailed)
            {
                result.Failed = true;
                var record = new MigrationPending
                {
                    SettingsFailed = result.SettingsFailed,
                    CarProfilesFailed = result.CarProfilesFailed,
                    SettingsMigrated = settingsMigrated,
                    CarProfiles = carProfiles,
                };
                JsonFile.WriteAllTextAtomic(pendingPath, JsonFile.Serialize(record));
                result.PendingWritten = true;
                log.Warn("Migration from Slip Lock Properties Calc incomplete; the failed part(s) are retried at the next start.");
                return result;
            }

            var marker = new MigrationMarker
            {
                FromSlipLockPropertiesCalc = (utcNow ?? (() => DateTime.UtcNow))().ToString(TimestampFormat, CultureInfo.InvariantCulture),
                Settings = settingsMigrated,
                CarProfiles = carProfiles,
            };
            JsonFile.WriteAllTextAtomic(markerPath, JsonFile.Serialize(marker));
            result.MarkerWritten = true;
            if (pending != null)
            {
                // The marker is checked first, so a pending record that cannot be deleted is harmless.
                TryDelete(pendingPath, log);
            }

            if (settingsMigrated || carProfiles > 0)
            {
                log.Info("Migration from Slip Lock Properties Calc done: settings " + (settingsMigrated ? "migrated" : "not migrated")
                    + ", " + carProfiles.ToString(CultureInfo.InvariantCulture) + " car profile(s). The old files are kept as a backup.");
            }
        }
        catch (Exception ex)
        {
            result.Failed = true;
            log.Error("Migration from Slip Lock Properties Calc failed: " + ex.Message);
        }

        return result;
    }

    /// <summary>
    /// Reads the pending record; null when there is none. An unreadable or corrupt record retries both parts, which is
    /// the safe side: the settings retry keeps the file it replaces, and the car merge never overwrites.
    /// </summary>
    private static MigrationPending ReadPending(string pendingPath, ILog log)
    {
        JsonReadStatus status = JsonFile.TryRead(pendingPath, out MigrationPending pending, out _, out string error);
        switch (status)
        {
            case JsonReadStatus.Missing:
                return null;
            case JsonReadStatus.Loaded:
                return pending;
            default:
                log.Warn("Pending migration record " + pendingPath + " is unreadable (" + error + "); retrying every part.");
                return new MigrationPending { SettingsFailed = true, CarProfilesFailed = true };
        }
    }

    private static void MigrateSettings(string pluginsDataDirectory, string hapticsDirectory, bool retry, ILog log, MigrationResult result)
    {
        string target = HapticsSettingsStore.GetPath(hapticsDirectory);
        string source = Path.Combine(pluginsDataDirectory, CommonFolderName, LegacySettingsFileName);
        try
        {
            if (!File.Exists(source) || (File.Exists(target) && !retry))
            {
                return;
            }

            string json = File.ReadAllText(source);
            HapticsSettings settings;
            try
            {
                // Same deserialization as SimHub's ReadCommonSettings used for this file.
                settings = JsonConvert.DeserializeObject<HapticsSettings>(json);
            }
            catch (JsonException ex)
            {
                // Not retried: the old file stays as it is, the plugin starts with default settings.
                log.Warn("Old settings " + source + " are not valid JSON (" + ex.Message + "); starting with default settings.");
                return;
            }

            if (settings == null)
            {
                log.Warn("Old settings " + source + " are empty; starting with default settings.");
                return;
            }

            settings.Normalize();
            if (File.Exists(target))
            {
                // Only on a retry: DLP wrote its own settings after the failed attempt. Keep them, just in case.
                string backup = target + PreMigrationSuffix;
                File.Copy(target, backup, overwrite: true);
                log.Info("Retrying the settings migration; the current " + target + " is kept as " + backup);
            }

            JsonFile.WriteAllTextAtomic(target, JsonFile.Serialize(settings));
            result.SettingsMigrated = true;
            log.Info("Migrated settings " + source + " to " + target);
        }
        catch (Exception ex)
        {
            // Every exception, not only IO: an unexpected one must neither skip the car profiles nor the retry.
            result.SettingsFailed = true;
            log.Error("Could not migrate settings " + source + ": " + ex.Message);
        }
    }

    private static void MigrateCarProfiles(string pluginsDataDirectory, string hapticsDirectory, bool retry, ILog log, MigrationResult result)
    {
        string target = Path.Combine(hapticsDirectory, CarFileNaming.CarsFolderName);
        string source = Path.Combine(pluginsDataDirectory, LegacyDataFolderName, CarFileNaming.CarsFolderName);
        string staging = target + StagingSuffix;
        try
        {
            if (!Directory.Exists(source) || (Directory.Exists(target) && !retry))
            {
                return;
            }

            if (Directory.Exists(staging))
            {
                // Leftover of an interrupted migration (our own folder): start over.
                Directory.Delete(staging, recursive: true);
            }

            if (Directory.Exists(target))
            {
                // Only on a retry: DLP has saved profiles since the failed attempt. Add what is missing, keep theirs.
                MergeTree(source, target, result);
                log.Info("Merged " + result.CarProfiles.ToString(CultureInfo.InvariantCulture) + " missing car profile(s) from " + source + " into " + target);
                return;
            }

            int profiles = CopyTree(source, staging);
            Directory.Move(staging, target);
            result.CarProfiles = profiles;
            log.Info("Migrated " + profiles.ToString(CultureInfo.InvariantCulture) + " car profile(s) from " + source + " to " + target);
        }
        catch (Exception ex)
        {
            // Every exception, not only IO (see MigrateSettings).
            result.CarProfilesFailed = true;
            log.Error("Could not migrate car profiles " + source + ": " + ex.Message);
        }
    }

    /// <summary>Copies every regular file of <paramref name="source"/> (recursively) and returns the number of profiles copied.</summary>
    private static int CopyTree(string source, string target)
    {
        int profiles = 0;
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            string name = Path.GetFileName(file);
            if (IsSkipped(name))
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, name), overwrite: false);
            if (name.EndsWith(ProfileExtension, StringComparison.OrdinalIgnoreCase))
            {
                profiles++;
            }
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            profiles += CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }

        return profiles;
    }

    /// <summary>
    /// Copies every regular file of <paramref name="source"/> (recursively) that <paramref name="target"/> does not have
    /// yet, each through a temporary file so a crash never leaves a partial profile under its real name. Counts the
    /// copied profiles in <paramref name="result"/> as it goes, so a failure keeps the count of what was copied.
    /// </summary>
    private static void MergeTree(string source, string target, MigrationResult result)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            string name = Path.GetFileName(file);
            string destination = Path.Combine(target, name);
            if (IsSkipped(name) || File.Exists(destination))
            {
                continue;
            }

            string temporary = destination + TempSuffix;
            File.Copy(file, temporary, overwrite: true);
            File.Move(temporary, destination);
            if (name.EndsWith(ProfileExtension, StringComparison.OrdinalIgnoreCase))
            {
                result.CarProfiles++;
            }
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            MergeTree(directory, Path.Combine(target, Path.GetFileName(directory)), result);
        }
    }

    private static void TryDelete(string path, ILog log)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (JsonFile.IsIoException(ex))
        {
            log.Warn("Could not delete " + path + ": " + ex.Message);
        }
    }

    /// <summary>Temporary files of interrupted writes, quarantined corrupt files and side files of unreadable profiles.</summary>
    private static bool IsSkipped(string fileName) =>
        fileName.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)
        || fileName.IndexOf(QuarantineInfix, StringComparison.OrdinalIgnoreCase) >= 0
        || fileName.EndsWith(UnsavedSuffix, StringComparison.OrdinalIgnoreCase);
}
