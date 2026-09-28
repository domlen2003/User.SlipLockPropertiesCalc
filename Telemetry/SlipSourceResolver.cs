using System;
using System.Collections.Generic;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Telemetry;

/// <summary>
/// Finds and reads the base wheel-slip signal (v1 <c>ProbeSlip</c>/<c>Res</c>/<c>TrySI</c>/<c>TryReadLMUSlip</c>)
/// and, new in v2, the ShakeIT <c>WheelLock</c> export.
/// <para>
/// Resolution order (v1): per wheel the first existing candidate of ShakeIT WheelSlip, ShakeIT proxyS, ACC native
/// WheelSlip (wheels may mix candidates); if any wheel has none, rFactor2/LMU wheel rotation when all four
/// <c>mRotation</c> values exist. Once resolved the source is kept until <see cref="Reset"/> (v1 ignored a ShakeIT
/// profile activated after rotation was chosen). Unlike v1, probing is rate limited instead of running every frame.
/// </para>
/// Owned by the data thread; all per-frame members are allocation-free.
/// </summary>
internal sealed class SlipSourceResolver
{
    /// <summary>Seconds between slip-source probes while unresolved (v1 probed every frame).</summary>
    public const double ProbeIntervalSeconds = 0.5;

    /// <summary>Seconds between WheelLock probes while missing (a ShakeIT profile may be activated later).</summary>
    public const double WheelLockProbeIntervalSeconds = 2.0;

    /// <summary>Tyre radius used when rFactor does not report one (v1: "~330 mm").</summary>
    public const double DefaultTireRadiusM = 0.33;

    /// <summary>Radius values above this are centimetres (the rF2 field is a byte in cm, e.g. 34).</summary>
    private const double RadiusCentimetreThreshold = 1.0;

    private const double CentimetresPerMetre = 100.0;

    /// <summary>ACC's WheelSlip ratio (0..~5) is scaled to the 0..100 range of the other sources (v1).</summary>
    private const double AccSlipScale = 20.0;

    /// <summary>Upper bound for scaled and computed slip percentages (v1).</summary>
    private const double SlipLimitPercent = 100.0;

    /// <summary>Below this vehicle speed (m/s) rotation slip is forced to 0 to avoid dividing by ~0 (v1).</summary>
    private const double MinRotationSpeedMs = 1.0;

    private const double Percent = 100.0;

    private const string AccNativeMarker = "Physics.WheelSlip";

    private readonly string[] slipPaths = new string[Wheels.Count];
    private readonly string[] lockPaths = new string[Wheels.Count];
    private readonly double[] tireRadii = new double[Wheels.Count];

    private double nextSlipProbeTime;
    private double nextLockProbeTime;

    public SlipSourceResolver()
    {
        Reset();
    }

    /// <summary>The resolved slip source (<see cref="SlipSourceKind.None"/> until resolved).</summary>
    public SlipSourceKind Kind { get; private set; }

    /// <summary>True once a slip source has been chosen for the current game.</summary>
    public bool IsResolved => Kind != SlipSourceKind.None;

    /// <summary>
    /// True for sources whose values are signed (positive = wheel spin, negative = lock): only rFactor rotation.
    /// ShakeIT and ACC native report unsigned magnitudes, so lock has to be synthesized from braking context.
    /// </summary>
    public bool IsSigned => Kind == SlipSourceKind.RFactorRotation;

    /// <summary>Resolved slip property path per wheel (null entries while unresolved; rotation paths for rFactor).</summary>
    public IReadOnlyList<string> ResolvedPaths => slipPaths;

    /// <summary>True once all four ShakeIT WheelLock properties exist.</summary>
    public bool HasWheelLock { get; private set; }

    /// <summary>ShakeIT WheelLock path per wheel (null entries while missing).</summary>
    public IReadOnlyList<string> WheelLockPaths => lockPaths;

    /// <summary>Tyre radius in metres used by the rotation source (only meaningful for <see cref="SlipSourceKind.RFactorRotation"/>).</summary>
    public double GetTireRadius(int wheel) => tireRadii[wheel];

    /// <summary>Forgets every resolution (game change) so the next <see cref="Probe"/> runs immediately.</summary>
    public void Reset()
    {
        Kind = SlipSourceKind.None;
        HasWheelLock = false;
        for (int i = 0; i < Wheels.Count; i++)
        {
            slipPaths[i] = null;
            lockPaths[i] = null;
            tireRadii[i] = DefaultTireRadiusM;
        }

        nextSlipProbeTime = double.NegativeInfinity;
        nextLockProbeTime = double.NegativeInfinity;
    }

    /// <summary>
    /// Resolves the slip source and the ShakeIT WheelLock export if still missing. Call once per frame; the actual
    /// property lookups are rate limited (<see cref="ProbeIntervalSeconds"/>, <see cref="WheelLockProbeIntervalSeconds"/>).
    /// </summary>
    /// <param name="reader">Property access.</param>
    /// <param name="wallTime">Monotonic wall-clock seconds.</param>
    /// <returns>True when the slip source or the WheelLock export was resolved during this call (log it).</returns>
    public bool Probe(ITelemetryReader reader, double wallTime)
    {
        bool changed = false;

        // "!(a < b)" keeps probing when the clock is NaN or jumps backwards instead of stalling forever.
        if (!IsResolved && !(wallTime < nextSlipProbeTime))
        {
            nextSlipProbeTime = wallTime + ProbeIntervalSeconds;
            changed |= ProbeSlip(reader);
        }

        if (!HasWheelLock && !(wallTime < nextLockProbeTime))
        {
            nextLockProbeTime = wallTime + WheelLockProbeIntervalSeconds;
            changed |= ProbeWheelLock(reader);
        }

        return changed;
    }

    /// <summary>
    /// Reads the base slip of all wheels into <paramref name="dest"/> in the source's native scale (v1):
    /// ShakeIT as-is (0..100), ACC native <c>Min(100, ratio x 20)</c>, rotation signed percent clamped to ±100
    /// (0 below 1 m/s). Non-finite property values read as 0.
    /// </summary>
    /// <param name="reader">Property access.</param>
    /// <param name="vehicleSpeedMs">Vehicle speed in m/s (SimHub <c>SpeedKmh / 3.6</c>).</param>
    /// <param name="dest">Length-4 destination; content is unspecified when false is returned.</param>
    /// <returns>False when no source is resolved or any wheel could not be read this frame.</returns>
    public bool TryRead(ITelemetryReader reader, double vehicleSpeedMs, double[] dest)
    {
        switch (Kind)
        {
            case SlipSourceKind.ShakeIt:
                return ReadAll(reader, slipPaths, dest);

            case SlipSourceKind.AccNative:
                if (!ReadAll(reader, slipPaths, dest))
                {
                    return false;
                }

                for (int i = 0; i < Wheels.Count; i++)
                {
                    dest[i] = Math.Min(SlipLimitPercent, dest[i] * AccSlipScale);
                }

                return true;

            case SlipSourceKind.RFactorRotation:
                return ReadRotationSlip(reader, vehicleSpeedMs, dest);

            default:
                return false;
        }
    }

    /// <summary>
    /// Reads ShakeIT's WheelLock export (0..100) into <paramref name="dest"/>. On failure <paramref name="dest"/> is
    /// zeroed so stale lock values can never leak into the pipeline.
    /// </summary>
    /// <returns>True when the export is resolved and all four wheels were read.</returns>
    public bool ReadWheelLock(ITelemetryReader reader, double[] dest)
    {
        if (HasWheelLock && ReadAll(reader, lockPaths, dest))
        {
            return true;
        }

        Array.Clear(dest, 0, Wheels.Count);
        return false;
    }

    /// <summary>v1 <c>ProbeSlip</c>: standard candidates first, rotation fallback.</summary>
    private bool ProbeSlip(ITelemetryReader reader)
    {
        if (TryResolveCandidates(reader))
        {
            // v1 classified the source by the front-left path only, so wheels that resolved to a different
            // candidate follow FL's scaling. Every candidate is either a ShakeIT or an ACC native path.
            bool accNative = slipPaths[Wheels.FrontLeft].IndexOf(AccNativeMarker, StringComparison.Ordinal) >= 0;
            Kind = accNative ? SlipSourceKind.AccNative : SlipSourceKind.ShakeIt;
            return true;
        }

        for (int i = 0; i < Wheels.Count; i++)
        {
            if (reader.GetValue(PropertyPaths.RFactorWheelRotation[i]) == null)
            {
                return false;
            }
        }

        for (int i = 0; i < Wheels.Count; i++)
        {
            slipPaths[i] = PropertyPaths.RFactorWheelRotation[i];
            tireRadii[i] = ReadTireRadius(reader, PropertyPaths.RFactorWheelRadius[i]);
        }

        Kind = SlipSourceKind.RFactorRotation;
        return true;
    }

    /// <summary>v1 <c>Res</c>: every wheel needs one existing candidate, otherwise nothing is kept.</summary>
    private bool TryResolveCandidates(ITelemetryReader reader)
    {
        for (int i = 0; i < Wheels.Count; i++)
        {
            string path = reader.ResolveFirst(PropertyPaths.SlipCandidates[i]);
            if (path == null)
            {
                Array.Clear(slipPaths, 0, Wheels.Count);
                return false;
            }

            slipPaths[i] = path;
        }

        return true;
    }

    private bool ProbeWheelLock(ITelemetryReader reader)
    {
        for (int i = 0; i < Wheels.Count; i++)
        {
            if (reader.GetValue(PropertyPaths.ShakeItWheelLock[i]) == null)
            {
                return false;
            }
        }

        for (int i = 0; i < Wheels.Count; i++)
        {
            lockPaths[i] = PropertyPaths.ShakeItWheelLock[i];
        }

        HasWheelLock = true;
        return true;
    }

    /// <summary>
    /// v1 radius rule: values above 1 are centimetres, otherwise metres; missing means 0.33 m. Zero, negative and
    /// non-finite values are also treated as missing (v1 would have produced a constant -100 % "lock").
    /// </summary>
    private static double ReadTireRadius(ITelemetryReader reader, string path)
    {
        if (!reader.TryGetDouble(path, out double raw) || !MathUtil.IsFinite(raw) || raw <= 0.0)
        {
            return DefaultTireRadiusM;
        }

        return raw > RadiusCentimetreThreshold ? raw / CentimetresPerMetre : raw;
    }

    /// <summary>v1 <c>TryReadLMUSlip</c>: (|omega| r - v) / v in percent, clamped to ±100.</summary>
    private bool ReadRotationSlip(ITelemetryReader reader, double vehicleSpeedMs, double[] dest)
    {
        // "!(v >= min)" also catches NaN speed.
        if (!(vehicleSpeedMs >= MinRotationSpeedMs))
        {
            Array.Clear(dest, 0, Wheels.Count);
            return true;
        }

        for (int i = 0; i < Wheels.Count; i++)
        {
            if (!reader.TryGetDouble(slipPaths[i], out double rotationRadPerSec))
            {
                return false;
            }

            if (!MathUtil.IsFinite(rotationRadPerSec))
            {
                dest[i] = 0.0;
                continue;
            }

            double wheelSpeed = Math.Abs(rotationRadPerSec) * tireRadii[i];
            dest[i] = MathUtil.Clamp((wheelSpeed - vehicleSpeedMs) / vehicleSpeedMs * Percent, -SlipLimitPercent, SlipLimitPercent);
        }

        return true;
    }

    /// <summary>Reads four resolved paths; non-finite values become 0 so NaN never reaches the exports.</summary>
    private static bool ReadAll(ITelemetryReader reader, string[] paths, double[] dest)
    {
        for (int i = 0; i < Wheels.Count; i++)
        {
            if (!reader.TryGetDouble(paths[i], out double value))
            {
                return false;
            }

            dest[i] = MathUtil.FiniteOr(value, 0.0);
        }

        return true;
    }
}
