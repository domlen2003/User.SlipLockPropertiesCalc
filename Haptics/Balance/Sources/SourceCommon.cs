using System.Text;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Haptics.Balance.Sources;

/// <summary>Behavior shared by all vehicle-state sources (DESIGN section 7, "Common").</summary>
internal static class SourceCommon
{
    /// <summary>SimHub prefix of the raw (sim-specific) telemetry object.</summary>
    public const string RawDataPrefix = "DataCorePlugin.GameRawData.";

    /// <summary>How often unresolved candidate paths are looked up again (seconds of wall time).</summary>
    public const double ProbeIntervalSeconds = 1.0;

    /// <summary>SimHub pedal values are percent.</summary>
    private const double PercentToUnit = 0.01;

    /// <summary>
    /// Clears <paramref name="state"/> and fills the fields every source takes from SimHub's normalized data:
    /// wall time, pedals (0..1), gear, replay/spectating/paused flags and the pit-lane flag.
    /// Sources then overwrite or OR in their raw values.
    /// </summary>
    public static void BeginRead(FrameContext ctx, VehicleState state)
    {
        state.Clear();
        state.WallTime = ctx.WallTime;
        state.Throttle = PedalFromPercent(ctx.Throttle);
        state.Brake = PedalFromPercent(ctx.Brake);
        state.Gear = ctx.GearNumber;
        state.IsReplay = ctx.IsReplay;
        state.IsSpectating = ctx.Spectating;
        state.IsPaused = ctx.GamePaused || ctx.GameInMenu;
        state.OnPitRoad = ctx.IsInPitLane;
    }

    /// <summary>
    /// Applies the validity rule (speed plus yaw rate, lateral acceleration or slip angles) and returns it.
    /// Without speed nothing can be normalized; without any lateral signal no detector can run.
    /// </summary>
    public static bool FinishRead(VehicleState state)
    {
        state.Valid = MathUtil.IsFinite(state.V)
            && (MathUtil.IsFinite(state.R) || MathUtil.IsFinite(state.Ay) || state.HasSlipAngles);
        return state.Valid;
    }

    /// <summary>Converts a SimHub percent pedal value to 0..1 (non-finite → 0).</summary>
    public static double PedalFromPercent(double percent) => MathUtil.Clamp01(MathUtil.FiniteOr(percent * PercentToUnit, 0.0));

    /// <summary>Clamps a 0..1 pedal value read from raw telemetry (non-finite → <paramref name="fallback"/>).</summary>
    public static double PedalOr(double unitValue, double fallback) =>
        MathUtil.IsFinite(unitValue) ? MathUtil.Clamp01(unitValue) : fallback;

    /// <summary>
    /// Rate limiter for probing unresolved paths. Returns true at most once per <see cref="ProbeIntervalSeconds"/>;
    /// also returns true when the wall clock went backwards (new clock origin), so probing never stalls.
    /// </summary>
    public static bool ProbeDue(double wallTime, ref double nextProbeWallTime)
    {
        if (wallTime >= nextProbeWallTime || wallTime < nextProbeWallTime - ProbeIntervalSeconds)
        {
            nextProbeWallTime = wallTime + ProbeIntervalSeconds;
            return true;
        }

        return false;
    }

    /// <summary>Resets every field of <paramref name="fields"/>.</summary>
    public static void ResetAll(SourceField[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i].Reset();
        }
    }

    /// <summary>Appends a diagnostics line per field (allocates; diagnostics only).</summary>
    public static void DescribeAll(StringBuilder builder, SourceField[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i].Describe(builder);
        }
    }

    /// <summary>Creates one field per wheel from <c>prefix + "01" + suffix</c> .. <c>"04"</c> (construction only).</summary>
    public static SourceField[] PerWheel(string label, string prefix, string suffix)
    {
        var fields = new SourceField[Wheels.Count];
        for (int i = 0; i < Wheels.Count; i++)
        {
            fields[i] = new SourceField(label + Wheels.ShortNames[i], prefix + Wheels.RawSuffixes[i] + suffix);
        }

        return fields;
    }
}
