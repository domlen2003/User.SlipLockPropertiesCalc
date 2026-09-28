using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Balance;

/// <summary>
/// Sim-independent vehicle state for one telemetry tick, produced by an <see cref="IVehicleStateSource"/>.
/// One instance is reused every frame (the source overwrites every field on each read).
/// Units are SI; lateral/yaw quantities use "left positive" as the intended convention, but the estimator
/// verifies and corrects the steering-vs-yaw and forward signs at runtime, so adapters use best-known conventions.
/// Unknown values are NaN.
/// </summary>
internal sealed class VehicleState
{
    /// <summary>True when the sample contains at least speed and yaw rate or slip angles.</summary>
    public bool Valid;

    /// <summary>Simulation time in seconds (iRacing SessionTime, rF2 mElapsedTime). NaN when the sim has none.</summary>
    public double SimTime = double.NaN;

    /// <summary>Monotonic wall-clock seconds (always set; used when <see cref="SimTime"/> is NaN and for freeze detection).</summary>
    public double WallTime;

    /// <summary>Longitudinal velocity in m/s (forward positive, before runtime forward-sign correction).</summary>
    public double V = double.NaN;

    /// <summary>Lateral velocity in m/s (sign irrelevant: only |beta| is used).</summary>
    public double Vy = double.NaN;

    /// <summary>In-game steering-wheel angle in radians (left positive intended; runtime-verified against yaw).</summary>
    public double Theta = double.NaN;

    /// <summary>Steering-wheel half-lock in radians (NaN when unknown). Used for default G derivation.</summary>
    public double ThetaMax = double.NaN;

    /// <summary>Yaw rate in rad/s. NaN when the sim has none (estimator then falls back to ay / v).</summary>
    public double R = double.NaN;

    /// <summary>Lateral acceleration in m/s² (sign irrelevant: only |ay| is used).</summary>
    public double Ay = double.NaN;

    /// <summary>Longitudinal acceleration in m/s².</summary>
    public double Ax = double.NaN;

    /// <summary>
    /// Vertical acceleration in m/s² as reported by the sim. Whether it includes gravity is auto-detected by the
    /// estimator (running mean ≈ g → includes gravity). NaN when unknown.
    /// </summary>
    public double Az = double.NaN;

    /// <summary>Throttle 0..1.</summary>
    public double Throttle;

    /// <summary>Brake 0..1.</summary>
    public double Brake;

    /// <summary>-1 reverse, 0 neutral, n forward.</summary>
    public int Gear;

    public SurfaceKind Surface;

    public bool OnPitRoad;

    /// <summary>Player car on track with physics running and the player in control.</summary>
    public bool OnTrack;

    public bool InGarage;

    public bool IsReplay;

    public bool IsSpectating;

    public bool IsPaused;

    /// <summary>Track wetness 0..1 (NaN when unknown).</summary>
    public double Wetness = double.NaN;

    /// <summary>Incremented by the source whenever the sim reports an impact (e.g. rF2 mLastImpactET change).</summary>
    public int ContactCounter;

    // ---- Direct slip-angle path (spec section 9) ----

    /// <summary>True when per-wheel slip angles are available this tick.</summary>
    public bool HasSlipAngles;

    /// <summary>Mean absolute front slip angle in the sim's native unit (radians for rF2/LMU; unit-agnostic downstream).</summary>
    public double AlphaFront = double.NaN;

    /// <summary>Mean absolute rear slip angle in the same unit as <see cref="AlphaFront"/>.</summary>
    public double AlphaRear = double.NaN;

    /// <summary>True when <see cref="AlphaFront"/>/<see cref="AlphaRear"/> are known to be radians (enables degree display).</summary>
    public bool SlipAnglesInRadians;

    /// <summary>Per-wheel signed slip angles (native unit), FL, FR, RL, RR. Debug only.</summary>
    public readonly double[] Alpha = new double[Wheels.Count];

    /// <summary>Resets every field to "unknown".</summary>
    public void Clear()
    {
        Valid = false;
        SimTime = double.NaN;
        V = double.NaN;
        Vy = double.NaN;
        Theta = double.NaN;
        ThetaMax = double.NaN;
        R = double.NaN;
        Ay = double.NaN;
        Ax = double.NaN;
        Az = double.NaN;
        Throttle = 0;
        Brake = 0;
        Gear = 0;
        Surface = SurfaceKind.Unknown;
        OnPitRoad = false;
        OnTrack = false;
        InGarage = false;
        IsReplay = false;
        IsSpectating = false;
        IsPaused = false;
        Wetness = double.NaN;
        HasSlipAngles = false;
        AlphaFront = double.NaN;
        AlphaRear = double.NaN;
        SlipAnglesInRadians = false;
        for (int i = 0; i < Wheels.Count; i++)
        {
            Alpha[i] = double.NaN;
        }

        // WallTime and ContactCounter are intentionally preserved (monotonic).
    }

    /// <summary>Copies all fields into <paramref name="target"/>.</summary>
    public void CopyTo(VehicleState target)
    {
        target.Valid = Valid;
        target.SimTime = SimTime;
        target.WallTime = WallTime;
        target.V = V;
        target.Vy = Vy;
        target.Theta = Theta;
        target.ThetaMax = ThetaMax;
        target.R = R;
        target.Ay = Ay;
        target.Ax = Ax;
        target.Az = Az;
        target.Throttle = Throttle;
        target.Brake = Brake;
        target.Gear = Gear;
        target.Surface = Surface;
        target.OnPitRoad = OnPitRoad;
        target.OnTrack = OnTrack;
        target.InGarage = InGarage;
        target.IsReplay = IsReplay;
        target.IsSpectating = IsSpectating;
        target.IsPaused = IsPaused;
        target.Wetness = Wetness;
        target.ContactCounter = ContactCounter;
        target.HasSlipAngles = HasSlipAngles;
        target.AlphaFront = AlphaFront;
        target.AlphaRear = AlphaRear;
        target.SlipAnglesInRadians = SlipAnglesInRadians;
        for (int i = 0; i < Wheels.Count; i++)
        {
            target.Alpha[i] = Alpha[i];
        }
    }
}
