using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Haptics.Settings;

namespace DivebombLogistics.Haptics.Telemetry;

/// <summary>
/// Tracks what the current game and car support regarding ABS/TC (v1 capability block):
/// whether the game exports ABS/TC levels, the current levels, and whether ABS/TC was ever seen active.
/// "Ever active" is persisted per game in <see cref="HapticsSettings.GameCapabilities"/> (ABSMode/TCMode =
/// "Available"), but, exactly like v1, only once that game already has an entry (created by the wheel-speed detection).
/// <para>
/// Persistence only mutates the settings object in memory and raises <see cref="SettingsChanged"/>; the shell
/// schedules the file save. Owned by the data thread; <see cref="Update"/> is allocation-free.
/// </para>
/// </summary>
internal sealed class CapabilityTracker
{
    /// <summary>Level reported when the game does not export ABS/TC (v1).</summary>
    public const double LevelNotExported = -1.0;

    private readonly HapticsSettings settings;
    private string gameName = string.Empty;

    public CapabilityTracker(HapticsSettings settings)
    {
        this.settings = settings;
    }

    /// <summary>True when <c>DataCorePlugin.GameData.ABSLevel</c> exists this frame.</summary>
    public bool GameExportsAbs { get; private set; }

    /// <summary>True when <c>DataCorePlugin.GameData.TCLevel</c> exists this frame.</summary>
    public bool GameExportsTc { get; private set; }

    /// <summary>ABS level, <see cref="LevelNotExported"/> when not exported.</summary>
    public double AbsLevel { get; private set; } = LevelNotExported;

    /// <summary>TC level, <see cref="LevelNotExported"/> when not exported.</summary>
    public double TcLevel { get; private set; } = LevelNotExported;

    /// <summary>ABS was seen active in this game (this session or persisted).</summary>
    public bool AbsEverActive { get; private set; }

    /// <summary>TC was seen active in this game (this session or persisted).</summary>
    public bool TcEverActive { get; private set; }

    /// <summary>v1 "Car has ABS": seen active → Yes; exported but never seen → Unknown; not exported → No.</summary>
    public TriState CarHasAbs => ToTriState(AbsEverActive, GameExportsAbs);

    /// <summary>v1 "Car has TC" (same rule as <see cref="CarHasAbs"/>).</summary>
    public TriState CarHasTc => ToTriState(TcEverActive, GameExportsTc);

    /// <summary>ABS is switched on in the car (exported and level &gt; 0); also the LockABS aggregate rule.</summary>
    public bool AbsEnabled => GameExportsAbs && AbsLevel > 0;

    /// <summary>TC is switched on in the car (exported and level &gt; 0); also the SlipTC aggregate rule.</summary>
    public bool TcEnabled => GameExportsTc && TcLevel > 0;

    /// <summary>True after <see cref="HapticsSettings.GameCapabilities"/> was modified; the shell saves and clears it.</summary>
    public bool SettingsChanged { get; private set; }

    public void ClearSettingsChanged() => SettingsChanged = false;

    /// <summary>Game change: forgets the ever-active flags and levels (v1).</summary>
    public void Reset(string game)
    {
        gameName = game ?? string.Empty;
        AbsEverActive = false;
        TcEverActive = false;
        GameExportsAbs = false;
        GameExportsTc = false;
        AbsLevel = LevelNotExported;
        TcLevel = LevelNotExported;
    }

    /// <summary>
    /// Per frame (before <see cref="WheelSpeedModeDetector.ComputeBaseSlip"/>, like v1): reads the levels,
    /// accumulates the ever-active flags and persists newly seen capabilities.
    /// </summary>
    public void Update(ITelemetryReader reader, bool absActive, bool tcActive)
    {
        GameExportsTc = reader.TryGetDouble(PropertyPaths.TcLevel, out double tc);
        TcLevel = GameExportsTc ? tc : LevelNotExported;
        GameExportsAbs = reader.TryGetDouble(PropertyPaths.AbsLevel, out double abs);
        AbsLevel = GameExportsAbs ? abs : LevelNotExported;

        if (absActive)
        {
            AbsEverActive = true;
        }

        if (tcActive)
        {
            TcEverActive = true;
        }

        Persist();
    }

    /// <summary>
    /// Applies the persisted capabilities of the current game (called by the detector's Loading step).
    /// Assignment, not OR, exactly like v1: <see cref="Update"/> runs first in the same frame and has already
    /// written any newly seen flag into <paramref name="persisted"/>, so nothing seen this session is lost.
    /// </summary>
    internal void LoadPersisted(GameCapabilities persisted)
    {
        AbsEverActive = persisted.ABSMode == GameCapabilities.Available;
        TcEverActive = persisted.TCMode == GameCapabilities.Available;
    }

    /// <summary>v1 <c>PersistDetection</c>.</summary>
    private void Persist()
    {
        if (!AbsEverActive && !TcEverActive)
        {
            return;
        }

        if (gameName.Length == 0
            || !settings.GameCapabilities.TryGetValue(gameName, out GameCapabilities entry)
            || entry == null)
        {
            return;
        }

        if (AbsEverActive && entry.ABSMode != GameCapabilities.Available)
        {
            entry.ABSMode = GameCapabilities.Available;
            SettingsChanged = true;
        }

        if (TcEverActive && entry.TCMode != GameCapabilities.Available)
        {
            entry.TCMode = GameCapabilities.Available;
            SettingsChanged = true;
        }
    }

    private static TriState ToTriState(bool everActive, bool exported)
    {
        if (everActive)
        {
            return TriState.Yes;
        }

        return exported ? TriState.Unknown : TriState.No;
    }
}
