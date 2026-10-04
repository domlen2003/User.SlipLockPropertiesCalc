namespace DivebombLogistics.Integration;

/// <summary>
/// Content of <c>PluginsData\DLP\migration-pending.json</c> (JSON field names are the file format): written by
/// <see cref="LegacyMigration"/> when a part failed, so the next start retries exactly that part even though DLP has
/// created its target files in the meantime. Deleted once the migration is complete (the marker is written).
/// </summary>
internal sealed class MigrationPending
{
    /// <summary>Migrating the settings failed; the next start migrates them even over an existing <c>Settings.json</c>.</summary>
    public bool SettingsFailed;

    /// <summary>Copying the car profiles failed; the next start merges the old tree into an existing <c>Cars</c> folder.</summary>
    public bool CarProfilesFailed;

    /// <summary>An earlier, incomplete run already migrated the settings (summed into the marker).</summary>
    public bool SettingsMigrated;

    /// <summary>Car profiles an earlier, incomplete run already copied (summed into the marker).</summary>
    public int CarProfiles;
}
