using System;
using System.Collections.Generic;
using System.Text;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Haptics.Balance.Sources;

/// <summary>
/// rFactor 2 / Le Mans Ultimate adapter over SimHub's rF2 shared-memory mirror
/// (<c>CurrentPlayerTelemetry</c> = rF2VehicleTelemetry, <c>CurrentPlayer</c> = rF2VehicleScoring).
/// </summary>
/// <remarks>
/// rF2's local vehicle frame has +z pointing rearward, so forward quantities are negated z components.
/// The contact patch velocities allow per-wheel slip angles, which enables the direct (slip-angle) path.
/// </remarks>
internal sealed class RFactorStateSource : IVehicleStateSource
{
    private const string TelemetryPrefix = SourceCommon.RawDataPrefix + "CurrentPlayerTelemetry.";
    private const string ScoringPrefix = SourceCommon.RawDataPrefix + "CurrentPlayer.";
    private const string WheelPrefix = TelemetryPrefix + "mWheels";

    /// <summary>Lock-to-lock steering range (deg) assumed when the car reports neither physical nor visual range.</summary>
    private const double DefaultSteeringRangeDeg = 540.0;

    /// <summary>Below this longitudinal ground speed (m/s) a wheel's slip angle is numerically meaningless.</summary>
    private const double MinWheelGroundSpeed = 1.0;

    /// <summary>Slip angles are only reported as usable above this vehicle speed (m/s).</summary>
    private const double MinSlipAngleSpeed = 5.0;

    // rF2VehicleScoring.mControl values.
    private const int ControlLocalPlayer = 0;
    private const int ControlReplay = 3;

    // rF2 surface type codes (rF2Wheel.mSurfaceType): 0 dry, 1 wet, 2 grass, 3 dirt, 4 gravel, 5 rumble strip, 6 special.
    // Codes 2..4 (grass, dirt, gravel) are the loose surfaces.
    private const int SurfaceWet = 1;
    private const int SurfaceGrass = 2;
    private const int SurfaceGravel = 4;
    private const int SurfaceRumbleStrip = 5;

    /// <summary>Wheels on grass/dirt/gravel needed to call the surface loose (two wheels off is already a different grip level).</summary>
    private const int LooseWheelThreshold = 2;

    /// <summary>Wheels on wet asphalt needed to call the surface wet (puddles under one wheel are not a wet track).</summary>
    private const int WetWheelThreshold = 3;

    private readonly SourceField elapsedTime = new SourceField("SimTime", TelemetryPrefix + "mElapsedTime");
    private readonly SourceField localVelX = new SourceField("Vy", TelemetryPrefix + "mLocalVel.x");
    private readonly SourceField localVelZ = new SourceField("V (-z)", TelemetryPrefix + "mLocalVel.z");
    private readonly SourceField localRotY = new SourceField("R", TelemetryPrefix + "mLocalRot.y");
    private readonly SourceField localAccelX = new SourceField("Ay", TelemetryPrefix + "mLocalAccel.x");
    private readonly SourceField localAccelY = new SourceField("Az", TelemetryPrefix + "mLocalAccel.y");
    private readonly SourceField localAccelZ = new SourceField("Ax (-z)", TelemetryPrefix + "mLocalAccel.z");
    private readonly SourceField steering = new SourceField("Steering", TelemetryPrefix + "mFilteredSteering");
    private readonly SourceField physicalRange = new SourceField("PhysicalRange", TelemetryPrefix + "mPhysicalSteeringWheelRange");
    private readonly SourceField visualRange = new SourceField("VisualRange", TelemetryPrefix + "mVisualSteeringWheelRange");
    private readonly SourceField throttle = new SourceField("Throttle", TelemetryPrefix + "mFilteredThrottle");
    private readonly SourceField brake = new SourceField("Brake", TelemetryPrefix + "mFilteredBrake");
    private readonly SourceField gear = new SourceField("Gear", TelemetryPrefix + "mGear");
    private readonly SourceField lastImpact = new SourceField("LastImpactET", TelemetryPrefix + "mLastImpactET");
    private readonly SourceField inGarageStall = new SourceField("InGarage", ScoringPrefix + "mInGarageStall");
    private readonly SourceField inPits = new SourceField("InPits", ScoringPrefix + "mInPits");
    private readonly SourceField control = new SourceField("Control", ScoringPrefix + "mControl");
    private readonly SourceField wetness = new SourceField("Wetness", SourceCommon.RawDataPrefix + "Data.mAvgPathWetness");
    private readonly SourceField[] surfaceType = SourceCommon.PerWheel("Surface", WheelPrefix, ".mSurfaceType");
    private readonly SourceField[] lateralPatchVel = SourceCommon.PerWheel("LatPatchVel", WheelPrefix, ".mLateralPatchVel");
    private readonly SourceField[] longitudinalGroundVel = SourceCommon.PerWheel("LongGroundVel", WheelPrefix, ".mLongitudinalGroundVel");
    private readonly SourceField[] allFields;

    private double nextProbeWallTime = double.NegativeInfinity;
    private double lastImpactTime = double.NaN;

    public RFactorStateSource()
    {
        var fields = new List<SourceField>
        {
            elapsedTime, localVelX, localVelZ, localRotY, localAccelX, localAccelY, localAccelZ, steering,
            physicalRange, visualRange, throttle, brake, gear, lastImpact, inGarageStall, inPits, control, wetness,
        };
        fields.AddRange(surfaceType);
        fields.AddRange(lateralPatchVel);
        fields.AddRange(longitudinalGroundVel);
        allFields = fields.ToArray();
    }

    public string Name => "rFactor2/LMU";

    public bool IsSupported => true;

    public bool Read(FrameContext ctx, ITelemetryReader reader, VehicleState state)
    {
        SourceCommon.BeginRead(ctx, state);
        bool probe = SourceCommon.ProbeDue(ctx.WallTime, ref nextProbeWallTime);

        state.SimTime = elapsedTime.Read(reader, probe);
        state.V = -localVelZ.Read(reader, probe);
        state.Vy = localVelX.Read(reader, probe);
        state.R = localRotY.Read(reader, probe);
        state.Ay = localAccelX.Read(reader, probe);
        state.Ax = -localAccelZ.Read(reader, probe);
        state.Az = localAccelY.Read(reader, probe);

        ReadSteering(reader, probe, state);

        state.Throttle = SourceCommon.PedalOr(throttle.Read(reader, probe), state.Throttle);
        state.Brake = SourceCommon.PedalOr(brake.Read(reader, probe), state.Brake);
        double gearValue = gear.Read(reader, probe);
        if (MathUtil.IsFinite(gearValue))
        {
            state.Gear = (int)gearValue;
        }

        ReadScoring(reader, probe, state);
        state.Surface = ClassifySurface(reader, probe);
        state.Wetness = wetness.Read(reader, probe);
        UpdateContact(reader, probe, state);
        ReadSlipAngles(reader, probe, state);

        return SourceCommon.FinishRead(state);
    }

    public void Reset()
    {
        SourceCommon.ResetAll(allFields);
        nextProbeWallTime = double.NegativeInfinity;
        lastImpactTime = double.NaN;
    }

    public string DescribeResolution()
    {
        var builder = new StringBuilder();
        builder.AppendLine("rFactor2/LMU (DataCorePlugin.GameRawData.CurrentPlayerTelemetry / CurrentPlayer)");
        SourceCommon.DescribeAll(builder, allFields);
        return builder.ToString();
    }

    private static bool IsSet(double flag) => MathUtil.IsFinite(flag) && flag != 0.0;

    /// <summary>
    /// mFilteredSteering is -1..1 of the physical lock with +1 = right; the state wants a left-positive
    /// steering-wheel angle, hence the negation. The estimator still verifies the sign against yaw.
    /// </summary>
    private void ReadSteering(ITelemetryReader reader, bool probe, VehicleState state)
    {
        double rangeDeg = physicalRange.Read(reader, probe);
        if (!(rangeDeg > 0.0))
        {
            rangeDeg = visualRange.Read(reader, probe);
            if (!(rangeDeg > 0.0))
            {
                rangeDeg = DefaultSteeringRangeDeg;
            }
        }

        double halfLockRad = rangeDeg * 0.5 * MathUtil.DegToRad;
        state.ThetaMax = halfLockRad;
        state.Theta = -steering.Read(reader, probe) * halfLockRad;
    }

    private void ReadScoring(ITelemetryReader reader, bool probe, VehicleState state)
    {
        state.InGarage = IsSet(inGarageStall.Read(reader, probe));
        state.OnPitRoad |= IsSet(inPits.Read(reader, probe));

        double controlValue = control.Read(reader, probe);
        if (!MathUtil.IsFinite(controlValue) || (int)controlValue == ControlLocalPlayer)
        {
            state.OnTrack = !state.InGarage;
        }
        else if ((int)controlValue == ControlReplay)
        {
            state.IsReplay = true;
        }

        // Nobody (-1), AI (1) or remote (2) in control: not the player's physics, OnTrack stays false.
    }

    private SurfaceKind ClassifySurface(ITelemetryReader reader, bool probe)
    {
        int known = 0;
        int loose = 0;
        int wet = 0;
        bool kerb = false;
        for (int i = 0; i < Wheels.Count; i++)
        {
            double code = surfaceType[i].Read(reader, probe);
            if (!MathUtil.IsFinite(code))
            {
                continue;
            }

            known++;
            int type = (int)code;
            if (type >= SurfaceGrass && type <= SurfaceGravel)
            {
                loose++;
            }
            else if (type == SurfaceRumbleStrip)
            {
                kerb = true;
            }
            else if (type == SurfaceWet)
            {
                wet++;
            }
        }

        if (known == 0)
        {
            return SurfaceKind.Unknown;
        }

        if (loose >= LooseWheelThreshold)
        {
            return SurfaceKind.Loose;
        }

        if (kerb)
        {
            return SurfaceKind.Kerb;
        }

        return wet >= WetWheelThreshold ? SurfaceKind.Wet : SurfaceKind.Asphalt;
    }

    /// <summary>
    /// mLastImpactET is the sim time of the most recent impact. The first observed value is only a baseline
    /// (it may be an old impact from before the plugin started); every later change is a new contact.
    /// </summary>
    private void UpdateContact(ITelemetryReader reader, bool probe, VehicleState state)
    {
        double impactTime = lastImpact.Read(reader, probe);
        if (!MathUtil.IsFinite(impactTime))
        {
            return;
        }

        if (MathUtil.IsFinite(lastImpactTime) && impactTime != lastImpactTime && impactTime > 0.0)
        {
            state.ContactCounter++;
        }

        lastImpactTime = impactTime;
    }

    private void ReadSlipAngles(ITelemetryReader reader, bool probe, VehicleState state)
    {
        int validWheels = 0;
        for (int i = 0; i < Wheels.Count; i++)
        {
            double lateral = lateralPatchVel[i].Read(reader, probe);
            double longitudinal = Math.Abs(longitudinalGroundVel[i].Read(reader, probe));
            if (MathUtil.IsFinite(lateral) && longitudinal > MinWheelGroundSpeed)
            {
                state.Alpha[i] = Math.Atan2(lateral, longitudinal);
                validWheels++;
            }
        }

        state.SlipAnglesInRadians = true;
        state.AlphaFront = MeanAbs(state.Alpha[Wheels.FrontLeft], state.Alpha[Wheels.FrontRight]);
        state.AlphaRear = MeanAbs(state.Alpha[Wheels.RearLeft], state.Alpha[Wheels.RearRight]);
        state.HasSlipAngles = validWheels == Wheels.Count && state.V > MinSlipAngleSpeed;
    }

    /// <summary>Mean of the magnitudes; NaN if either is unknown (propagates naturally).</summary>
    private static double MeanAbs(double a, double b) => (Math.Abs(a) + Math.Abs(b)) * 0.5;
}
