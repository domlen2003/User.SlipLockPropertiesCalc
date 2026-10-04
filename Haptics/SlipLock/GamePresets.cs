using System;
using System.Collections.Generic;

namespace DivebombLogistics.Haptics.SlipLock;

/// <summary>
/// Hardcoded per-game presets. Values are unchanged from v1; tweak them here.
/// Lookup is case-insensitive: SimHub reports e.g. "AssettoCorsaEVO" / "BeamNgDrive", which the
/// case-sensitive v1 table never matched (those games silently fell back to <see cref="Default"/>).
/// </summary>
internal static class GamePresets
{
    public const string DefaultName = "Default";

    private static readonly Dictionary<string, GamePreset> Table = new Dictionary<string, GamePreset>(StringComparer.OrdinalIgnoreCase)
    {
        // iRacing: mono slip only, no per-wheel, no TC. Heavy proxyL.
        { "IRacing", new GamePreset(50, 50, 50, 50, 50, 50, 50, 50, true) },

        // ACC: ShakeIT slip goes positive for both spin and lock. Synth lock from braking context.
        { "AssettoCorsaCompetizione", new GamePreset(10, 10, 10, 10, 50, 50, 50, 50, true) },

        // AC: same as ACC.
        { "AssettoCorsa", new GamePreset(10, 10, 10, 10, 50, 50, 50, 50, true) },

        // AC Evo: likely same as AC.
        { "AssettoCorsaEvo", new GamePreset(10, 10, 10, 10, 50, 50, 50, 50, true) },

        // LMU: rotation-based slip already includes tire loading effects, so inverse proxyL on slip.
        // Precut 15% removes the noise floor; speed fade below 70 km/h.
        { "LMU", new GamePreset(15, 15, 15, 15, 50, 50, 50, 50, true, 70, true, 100, 15) },

        // rFactor 2: same engine as LMU.
        { "RFactor2", new GamePreset(15, 15, 15, 15, 50, 50, 50, 50, true, 70, true, 100, 15) },

        // AMS2.
        { "AMS2", new GamePreset(15, 15, 15, 15, 50, 50, 50, 50, true) },

        // BeamNG.
        { "BeamNGdrive", new GamePreset(15, 15, 15, 15, 50, 50, 50, 50, true) },
    };

    /// <summary>Fallback for unknown games: synth lock from slip (most games), moderate proxyL.</summary>
    public static readonly GamePreset Default = new GamePreset(30, 30, 30, 30, 50, 50, 50, 50, true);

    /// <summary>Returns the preset for a SimHub game name and the table key used (or <see cref="DefaultName"/>).</summary>
    public static GamePreset Get(string gameName, out string presetName)
    {
        if (!string.IsNullOrEmpty(gameName) && Table.TryGetValue(gameName, out GamePreset preset))
        {
            foreach (KeyValuePair<string, GamePreset> entry in Table)
            {
                if (ReferenceEquals(entry.Value, preset))
                {
                    presetName = entry.Key;
                    return preset;
                }
            }
        }

        presetName = DefaultName;
        return Default;
    }
}
