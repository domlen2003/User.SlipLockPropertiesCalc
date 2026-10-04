namespace DivebombLogistics.Integration;

/// <summary>
/// Content of <c>PluginsData\DLP\migration.json</c> (JSON field names are the file format). Its existence alone makes
/// later starts skip <see cref="LegacyMigration"/>.
/// </summary>
internal sealed class MigrationMarker
{
    /// <summary>UTC time of the migration (ISO 8601).</summary>
    public string FromSlipLockPropertiesCalc = string.Empty;

    /// <summary>The settings were migrated.</summary>
    public bool Settings;

    /// <summary>Number of car profiles copied.</summary>
    public int CarProfiles;
}
