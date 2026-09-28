using System;
using System.Text;
using User.SlipLockPropertiesCalc.Core;
using User.SlipLockPropertiesCalc.Telemetry;

namespace User.SlipLockPropertiesCalc.Balance.Sources;

/// <summary>
/// iRacing adapter (<c>DataCorePlugin.GameRawData.Telemetry.*</c>, irsdk variables in SI units).
/// iRacing has no tyre slip in live telemetry, so this source only feeds the bicycle-model path.
/// </summary>
internal sealed class IRacingStateSource : IVehicleStateSource
{
    private const string Prefix = SourceCommon.RawDataPrefix + "Telemetry.";

    /// <summary>Half-lock values below this (rad, ≈ 6°) are placeholders, not a real steering range.</summary>
    private const double MinPlausibleHalfLockRad = 0.1;

    /// <summary>Only frames faster than this (m/s) take part in the VelocityX frame check (heading noise dominates below).</summary>
    private const double FrameCheckMinSpeed = 20.0;

    /// <summary>Number of fast frames averaged before deciding whether VelocityX is a car-frame velocity.</summary>
    private const int FrameCheckSamples = 60;

    /// <summary>
    /// In the car frame |VelocityX| ≈ Speed (body slip is a few degrees). A world-frame X component averages
    /// far lower over heading changes, so a mean ratio below this means VelocityX/Y are not usable.
    /// </summary>
    private const double FrameCheckMinRatio = 0.7;

    private readonly ILog log;

    private readonly SourceField sessionTime = new SourceField("SimTime", Prefix + "SessionTime");
    private readonly SourceField velocityX = new SourceField("V", Prefix + "VelocityX");
    private readonly SourceField velocityY = new SourceField("Vy", Prefix + "VelocityY");
    private readonly SourceField speed = new SourceField("Speed", Prefix + "Speed");
    private readonly SourceField steering = new SourceField("Theta", Prefix + "SteeringWheelAngle");
    private readonly SourceField steeringMax = new SourceField("ThetaMax", Prefix + "SteeringWheelAngleMax");
    private readonly SourceField yawRate = new SourceField("R", Prefix + "YawRate");
    private readonly SourceField latAccel = new SourceField("Ay", Prefix + "LatAccel");
    private readonly SourceField longAccel = new SourceField("Ax", Prefix + "LongAccel");
    private readonly SourceField vertAccel = new SourceField("Az", Prefix + "VertAccel");
    private readonly SourceField onPitRoad = new SourceField("OnPitRoad", Prefix + "OnPitRoad");
    private readonly SourceField inGarage = new SourceField("InGarage", Prefix + "IsInGarage");
    private readonly SourceField onTrack = new SourceField("OnTrack", Prefix + "IsOnTrack", Prefix + "IsOnTrackCar");
    private readonly SourceField replayPlaying = new SourceField("IsReplay", Prefix + "IsReplayPlaying");
    private readonly SourceField[] allFields;

    private double nextProbeWallTime = double.NegativeInfinity;
    private FrameCheck frameCheck;
    private int frameCheckCount;
    private double frameCheckRatioSum;

    public IRacingStateSource(ILog log = null)
    {
        this.log = log ?? NullLog.Instance;
        allFields = new[]
        {
            sessionTime, velocityX, velocityY, speed, steering, steeringMax, yawRate, latAccel, longAccel, vertAccel,
            onPitRoad, inGarage, onTrack, replayPlaying,
        };
    }

    /// <summary>Outcome of the VelocityX car-frame sanity check.</summary>
    private enum FrameCheck
    {
        Pending = 0,
        CarFrame,
        NotCarFrame,
    }

    public string Name => "iRacing";

    public bool IsSupported => true;

    /// <summary>True once VelocityX was found not to be a car-frame velocity (V then comes from Speed, Vy is unknown).</summary>
    public bool UsesSpeedFallback => frameCheck == FrameCheck.NotCarFrame;

    public bool Read(FrameContext ctx, ITelemetryReader reader, VehicleState state)
    {
        SourceCommon.BeginRead(ctx, state);
        bool probe = SourceCommon.ProbeDue(ctx.WallTime, ref nextProbeWallTime);

        state.SimTime = sessionTime.Read(reader, probe);

        double vx = velocityX.Read(reader, probe);
        double vy = velocityY.Read(reader, probe);
        double speedMs = speed.Read(reader, probe);
        UpdateFrameCheck(vx, speedMs);
        if (frameCheck == FrameCheck.NotCarFrame || !MathUtil.IsFinite(vx))
        {
            // Speed is a magnitude: fine for the model, but it carries no lateral component.
            state.V = speedMs;
            state.Vy = double.NaN;
        }
        else
        {
            state.V = vx;
            state.Vy = vy;
        }

        state.Theta = steering.Read(reader, probe);
        double halfLock = steeringMax.Read(reader, probe);
        state.ThetaMax = halfLock > MinPlausibleHalfLockRad ? halfLock : double.NaN;
        state.R = yawRate.Read(reader, probe);
        state.Ay = latAccel.Read(reader, probe);
        state.Ax = longAccel.Read(reader, probe);
        state.Az = vertAccel.Read(reader, probe);

        state.OnPitRoad |= IsSet(onPitRoad.Read(reader, probe));
        state.InGarage = IsSet(inGarage.Read(reader, probe));

        // Older SimHub wrappers lack IsOnTrack; assume on track rather than silencing the outputs forever.
        double onTrackValue = onTrack.Read(reader, probe);
        state.OnTrack = !MathUtil.IsFinite(onTrackValue) || onTrackValue != 0.0;
        state.IsReplay |= IsSet(replayPlaying.Read(reader, probe));
        state.Surface = SurfaceKind.Unknown;

        return SourceCommon.FinishRead(state);
    }

    public void Reset()
    {
        SourceCommon.ResetAll(allFields);
        nextProbeWallTime = double.NegativeInfinity;
        frameCheck = FrameCheck.Pending;
        frameCheckCount = 0;
        frameCheckRatioSum = 0.0;
    }

    public string DescribeResolution()
    {
        var builder = new StringBuilder();
        builder.AppendLine("iRacing (DataCorePlugin.GameRawData.Telemetry)");
        SourceCommon.DescribeAll(builder, allFields);
        builder.Append("  VelocityX frame check: ");
        switch (frameCheck)
        {
            case FrameCheck.CarFrame:
                builder.AppendLine("car frame (V = VelocityX)");
                break;
            case FrameCheck.NotCarFrame:
                builder.AppendLine("not car frame (V = Speed, body slip unavailable)");
                break;
            default:
                builder.Append("pending (").Append(frameCheckCount).Append('/').Append(FrameCheckSamples).AppendLine(" fast samples)");
                break;
        }

        return builder.ToString();
    }

    private static bool IsSet(double flag) => MathUtil.IsFinite(flag) && flag != 0.0;

    /// <summary>
    /// SimHub's iRacing wrapper documents VelocityX/Y as car-frame, but if they were world-frame the body slip and
    /// forward speed would be garbage. Decide once from fast frames; the decision holds until <see cref="Reset"/>.
    /// </summary>
    private void UpdateFrameCheck(double vx, double speedMs)
    {
        if (frameCheck != FrameCheck.Pending || !MathUtil.IsFinite(vx) || !(speedMs > FrameCheckMinSpeed))
        {
            return;
        }

        frameCheckRatioSum += Math.Abs(vx) / speedMs;
        frameCheckCount++;
        if (frameCheckCount < FrameCheckSamples)
        {
            return;
        }

        if (frameCheckRatioSum / frameCheckCount < FrameCheckMinRatio)
        {
            frameCheck = FrameCheck.NotCarFrame;
            log.Warn("Balance/iRacing: VelocityX does not match Speed (not a car-frame velocity); using Speed, body slip disabled.");
        }
        else
        {
            frameCheck = FrameCheck.CarFrame;
        }
    }
}
