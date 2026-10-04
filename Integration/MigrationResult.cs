namespace DivebombLogistics.Integration;

/// <summary>What <see cref="LegacyMigration.Run"/> did in this run (for the log and tests).</summary>
internal sealed class MigrationResult
{
    /// <summary>The marker existed: nothing was checked or copied.</summary>
    public bool AlreadyMigrated { get; set; }

    /// <summary>A pending record of an earlier failed run existed, so its failed parts were retried.</summary>
    public bool Retry { get; set; }

    /// <summary>The old settings were converted to <c>Haptics\Settings.json</c>.</summary>
    public bool SettingsMigrated { get; set; }

    /// <summary>Number of car profile files copied in this run.</summary>
    public int CarProfiles { get; set; }

    /// <summary>The settings part failed.</summary>
    public bool SettingsFailed { get; set; }

    /// <summary>The car profile part failed.</summary>
    public bool CarProfilesFailed { get; set; }

    /// <summary>Something failed; the marker was not written so the next start retries.</summary>
    public bool Failed { get; set; }

    /// <summary>The pending record (<see cref="LegacyMigration.PendingFileName"/>) was written.</summary>
    public bool PendingWritten { get; set; }

    /// <summary>The marker file was written.</summary>
    public bool MarkerWritten { get; set; }
}
