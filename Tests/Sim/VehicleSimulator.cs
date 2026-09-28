using System;
using System.Collections.Generic;
using User.SlipLockPropertiesCalc.Balance;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Tests.Sim;

/// <summary>
/// "True" parameters of the simulated car: what a perfect estimator would discover. Defaults are a GT-like car
/// matching the estimator's car-agnostic defaults (G, K, yaw lag), so scenarios are not polluted by model error.
/// </summary>
internal sealed class SimCar
{
    private static readonly BalanceTuning EstimatorDefaults = new BalanceTuning();

    /// <summary>Steering-wheel angle / road-wheel angle (default ≈ 14.2, so that G equals <see cref="BalanceTuning.GDefault"/>).</summary>
    public double SteeringRatio = 1.0 / (EstimatorDefaults.GDefault * EstimatorDefaults.WheelbaseDefaultM);

    public double WheelbaseM = EstimatorDefaults.WheelbaseDefaultM;

    /// <summary>Understeer factor K (s²/m²) of the linear yaw response.</summary>
    public double K = EstimatorDefaults.KDefault;

    /// <summary>Steering-wheel angle (deg) needed to drive straight (wheel offset, bent steering).</summary>
    public double Theta0Deg;

    /// <summary>First-order lag (s) of the yaw rate behind the steering.</summary>
    public double TauYaw = EstimatorDefaults.TauYawDefault;

    /// <summary>Front axle grip limit (m/s²): the path cannot curve faster than this / v (understeer).</summary>
    public double FrontGripLimit = 14.0;

    /// <summary>Normal (non-sliding) body slip in degrees per m/s² of lateral acceleration.</summary>
    public double BodySlipDegPerAy = 0.1;

    /// <summary>Front / rear tyre slip angle in radians per m/s² of lateral acceleration (direct path; nearly neutral).</summary>
    public double FrontSlipPerAy = 0.0065;

    public double RearSlipPerAy = 0.006;

    /// <summary>Time constant (s) with which a rear slide relaxes once the extra yaw stops.</summary>
    public double SlideRecoveryTau = 0.5;

    /// <summary>Steering-wheel half-lock reported to the estimator (deg); NaN = not reported.</summary>
    public double HalfLockDeg = double.NaN;

    /// <summary>Combined steering gain G = 1 / (ratio · wheelbase) in 1/m.</summary>
    public double G => 1.0 / (SteeringRatio * WheelbaseM);

    /// <summary>Steering-wheel angle (deg, relative to <see cref="Theta0Deg"/>) giving lateral acceleration <paramref name="ay"/> at <paramref name="speed"/> in the linear range.</summary>
    public double SteerDegFor(double ay, double speed) => ay * (1.0 + (K * speed * speed)) / (G * speed * speed) * MathUtil.RadToDeg;

    /// <summary>Steering-wheel angle (deg) at which the front axle saturates at <paramref name="speed"/>.</summary>
    public double GripLimitSteerDeg(double speed) => SteerDegFor(FrontGripLimit, speed);
}

/// <summary>Gaussian noise standard deviations per reported channel (SI units, radians).</summary>
internal sealed class SimNoise
{
    public double YawRate;
    public double Steering;
    public double Speed;
    public double LateralVelocity;
    public double LateralAccel;
    public double LongitudinalAccel;
    public double VerticalAccel;
    public double SlipAngle;

    public static SimNoise None() => new SimNoise();

    /// <summary>Noise levels comparable to sim telemetry (physics outputs are clean; accelerations vibrate).</summary>
    public static SimNoise Typical() => new SimNoise
    {
        YawRate = 0.003,
        Steering = 0.001,
        Speed = 0.03,
        LateralVelocity = 0.02,
        LateralAccel = 0.2,
        LongitudinalAccel = 0.2,
        VerticalAccel = 0.3,
        SlipAngle = 0.0005,
    };
}

/// <summary>How the simulated sim reports its telemetry (conventions, available channels, rate).</summary>
internal sealed class SimOptions
{
    public double RateHz = 60.0;

    /// <summary>Report the steering angle with the opposite sign (adapter convention wrong).</summary>
    public bool InvertSteering;

    /// <summary>Report the longitudinal velocity with the opposite sign (adapter convention wrong).</summary>
    public bool InvertForward;

    /// <summary>True: Az ≈ +g at rest (accelerometer convention); false: Az ≈ 0 at rest (kinematic).</summary>
    public bool GravityIncluded = true;

    /// <summary>False: SimTime is NaN and the estimator runs on wall time.</summary>
    public bool ReportSimTime = true;

    /// <summary>False: R is NaN (estimator falls back to ay / v).</summary>
    public bool ReportYawRate = true;

    public bool ReportLateralVelocity = true;

    /// <summary>Report front/rear slip angles in radians (direct path).</summary>
    public bool ReportSlipAngles;

    public double StartSimTime = 100.0;
    public double StartWallTime = 5000.0;
}

/// <summary>
/// One scripted phase: driver inputs ramp linearly from the Start to the End values over <see cref="Duration"/>.
/// Flags and events apply for the whole segment (events such as a collision fire on its first tick).
/// </summary>
internal sealed class SimSegment
{
    public string Label = string.Empty;
    public double Duration;

    /// <summary>Steering-wheel angle in degrees relative to the car's offset (left positive).</summary>
    public double SteerStartDeg;

    public double SteerEndDeg;

    /// <summary>Planar speed in m/s (negative = rolling backwards).</summary>
    public double SpeedStart;

    public double SpeedEnd;

    public double ThrottleStart;
    public double ThrottleEnd;
    public double BrakeStart;
    public double BrakeEnd;

    /// <summary>Extra yaw rate (rad/s, positive = further into a left turn) from rear grip loss (oversteer).</summary>
    public double SlideYawStart;

    public double SlideYawEnd;

    /// <summary>Keep the path curvature of the segment start while steering is free (countersteering a slide).</summary>
    public bool HoldPath;

    public int Gear = 3;
    public SurfaceKind Surface = SurfaceKind.Asphalt;
    public double Wetness = double.NaN;
    public bool PitLane;
    public bool Replay;
    public bool Spectating;
    public bool Paused;

    /// <summary>In the garage / not in control (OnTrack false, InGarage true).</summary>
    public bool NotOnTrack;

    /// <summary>Wheels off the ground: vertical specific force drops to 0.</summary>
    public bool Airborne;

    /// <summary>Telemetry frozen: every field repeats (SimTime included) while wall time runs on.</summary>
    public bool Frozen;

    /// <summary>The source reports an invalid sample.</summary>
    public bool Invalid;

    /// <summary>Single-sample kerb spikes on the reported yaw rate every this many seconds (0 = none).</summary>
    public double KerbSpikeInterval;

    public double KerbSpikeAmplitude = 1.5;

    /// <summary>Lateral acceleration spike (in g) on the first tick (collision).</summary>
    public double CollisionG;

    /// <summary>The sim reports an impact on the first tick (ContactCounter increments).</summary>
    public bool ContactEvent;

    /// <summary>Sim and wall clock jump by this many seconds on the first tick (negative = session restart).</summary>
    public double TimeJump;

    /// <summary>The car is moved on the first tick: speed jumps to <see cref="SpeedStart"/>, yaw and slide reset.</summary>
    public bool Teleport;

    /// <summary>Slip angles are reported during this segment (when <see cref="SimOptions.ReportSlipAngles"/>).</summary>
    public bool SlipAnglesAvailable = true;

    /// <summary>Sets constant pedal positions for the whole segment.</summary>
    public SimSegment Pedals(double throttle, double brake)
    {
        ThrottleStart = throttle;
        ThrottleEnd = throttle;
        BrakeStart = brake;
        BrakeEnd = brake;
        return this;
    }

    /// <summary>Applies arbitrary settings (flags/events) and returns the segment for chaining.</summary>
    public SimSegment With(Action<SimSegment> configure)
    {
        configure(this);
        return this;
    }
}

/// <summary>
/// Fluent driver script: each call appends a segment that starts from where the previous one ended
/// (steering angle, speed and slide yaw rate), so scenarios read like a lap description.
/// </summary>
internal sealed class SimScript
{
    private const double DefaultCruiseThrottle = 0.4;
    private const double DefaultBrake = 0.6;

    private readonly List<SimSegment> segments = new List<SimSegment>();

    public SimScript(double initialSpeed, double initialSteerDeg = 0.0)
    {
        Speed = initialSpeed;
        SteerDeg = initialSteerDeg;
    }

    public IReadOnlyList<SimSegment> Segments => segments;

    /// <summary>Speed (m/s) at the end of the script so far.</summary>
    public double Speed { get; private set; }

    /// <summary>Steering angle (deg) at the end of the script so far.</summary>
    public double SteerDeg { get; private set; }

    /// <summary>Extra slide yaw rate (rad/s) at the end of the script so far.</summary>
    public double SlideYaw { get; private set; }

    /// <summary>Total scripted time (s).</summary>
    public double Duration { get; private set; }

    /// <summary>
    /// Ramps steering, speed and (optionally) the slide yaw rate linearly to the given values. Pedals default to
    /// full throttle when speeding up, braking when slowing down and part throttle otherwise.
    /// </summary>
    /// <param name="slideYaw">Slide yaw rate at the end of the segment (rad/s); NaN keeps the current value.</param>
    public SimSegment To(double duration, double steerDeg, double speed, string label = null, double slideYaw = double.NaN)
    {
        double slideEnd = double.IsNaN(slideYaw) ? SlideYaw : slideYaw;
        var segment = new SimSegment
        {
            Label = label ?? string.Empty,
            Duration = duration,
            SteerStartDeg = SteerDeg,
            SteerEndDeg = steerDeg,
            SpeedStart = Speed,
            SpeedEnd = speed,
            SlideYawStart = SlideYaw,
            SlideYawEnd = slideEnd,
        };

        if (speed > Speed)
        {
            segment.Pedals(1.0, 0.0);
        }
        else if (speed < Speed)
        {
            segment.Pedals(0.0, DefaultBrake);
        }
        else
        {
            segment.Pedals(DefaultCruiseThrottle, 0.0);
        }

        segments.Add(segment);
        SteerDeg = steerDeg;
        Speed = speed;
        SlideYaw = slideEnd;
        Duration += duration;
        return segment;
    }

    /// <summary>Keeps steering, speed and slide.</summary>
    public SimSegment Hold(double duration, string label = null) => To(duration, SteerDeg, Speed, label);

    /// <summary>Ramps the steering, keeps the speed.</summary>
    public SimSegment Steer(double duration, double steerDeg, string label = null) => To(duration, steerDeg, Speed, label);

    /// <summary>Ramps the speed, keeps the steering.</summary>
    public SimSegment Drive(double duration, double speed, string label = null) => To(duration, SteerDeg, speed, label);

    /// <summary>Ramps the slide yaw rate (rear grip loss), keeps steering and speed.</summary>
    public SimSegment SlideTo(double duration, double slideYaw, string label = null) => To(duration, SteerDeg, Speed, label, slideYaw);

    /// <summary>Teleports the car (tow, reset to pits): speed jumps to <paramref name="speed"/>, steering and slide to 0.</summary>
    public SimSegment TeleportTo(double duration, double speed, string label = null)
    {
        Speed = speed;
        SteerDeg = 0.0;
        SlideYaw = 0.0;
        SimSegment segment = Hold(duration, label);
        segment.Teleport = true;
        return segment;
    }
}

/// <summary>
/// Deterministic single-track car driven by a <see cref="SimScript"/>, producing one <see cref="VehicleState"/>
/// per tick exactly like a sim adapter would. Model:
/// <list type="bullet">
/// <item>Yaw response: r_lin = G·v·θ / (1 + K·v²), capped by the front grip limit (understeer), then a first-order
/// lag with <see cref="SimCar.TauYaw"/> (exact exponential discretization).</item>
/// <item>Rear grip loss: scripted extra yaw rate accumulates as body slip, which relaxes with
/// <see cref="SimCar.SlideRecoveryTau"/>; the path keeps following the tyres (ay = v·r_path).</item>
/// <item>Countersteer: <see cref="SimSegment.HoldPath"/> decouples the path from the steering.</item>
/// <item>Slip angles: linear in |ay|, plus the steering beyond the grip limit at the front and the slide at the rear.</item>
/// </list>
/// Allocation-free per <see cref="Step"/>, so it can drive allocation tests.
/// </summary>
internal sealed class VehicleSimulator
{
    private const double MinGripSpeed = 1.0;
    private const double MinSlipAngleSpeed = 5.0;

    private readonly SimSegment[] segments;
    private readonly SimNoise noise;
    private readonly Random random;
    private readonly double dt;

    private int segmentIndex = -1;
    private int segmentTick;
    private int segmentTicks;
    private double simClock;
    private double wallClock;
    private int contactCounter;
    private int kerbSign = 1;

    // ---- Vehicle state ----
    private double previousSpeed = double.NaN;
    private double pathRate;
    private double heldPathRate;
    private bool holdingPath;
    private double slideAngle;

    // ---- Kinematics of the current tick (written by Integrate, reported by Write) ----
    private double steerRad;
    private double longitudinalAccel;
    private double alphaFront;
    private double alphaRear;

    public VehicleSimulator(SimCar car, SimScript script, SimNoise noise = null, SimOptions options = null, int seed = 12345)
    {
        Car = car ?? new SimCar();
        Options = options ?? new SimOptions();
        this.noise = noise ?? SimNoise.None();
        random = new Random(seed);
        dt = 1.0 / Options.RateHz;
        segments = new SimSegment[script.Segments.Count];
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = script.Segments[i];
        }

        simClock = Options.StartSimTime;
        wallClock = Options.StartWallTime;
    }

    public SimCar Car { get; }

    public SimOptions Options { get; }

    /// <summary>The state written by the last <see cref="Step"/> (one instance, overwritten every tick).</summary>
    public VehicleState State { get; } = new VehicleState();

    /// <summary>Segment of the last tick (null before the first and after the last tick).</summary>
    public SimSegment Segment { get; private set; }

    /// <summary>Seconds since the start of the current segment (end of the last tick).</summary>
    public double SegmentTime => segmentTick * dt;

    /// <summary>Simulated seconds since the start of the script (clock jumps excluded).</summary>
    public double Time { get; private set; }

    public long Ticks { get; private set; }

    // ---- Ground truth of the last tick (for assertions) ----
    public double TrueYawRate { get; private set; }
    public double TrueLateralAccel { get; private set; }
    public double TrueBodySlipDeg { get; private set; }
    public double TrueSpeed { get; private set; }

    /// <summary>Advances one tick. Returns false (state untouched) when the script has ended.</summary>
    public bool Step()
    {
        if (!EnterSegmentIfNeeded())
        {
            Segment = null;
            return false;
        }

        SimSegment segment = segments[segmentIndex];
        bool first = segmentTick == 0;
        segmentTick++;
        Ticks++;
        Time += dt;
        wallClock += dt;
        if (first)
        {
            simClock += segment.TimeJump;
            wallClock += segment.TimeJump;
            if (segment.ContactEvent)
            {
                contactCounter++;
            }
        }

        Segment = segment;
        if (segment.Frozen)
        {
            // A frozen telemetry buffer repeats every value; only the consumer's wall clock advances.
            State.WallTime = wallClock;
            return true;
        }

        simClock += dt;
        double progress = (double)segmentTick / segmentTicks;
        Integrate(segment, progress, first);
        Write(segment, progress, first);
        return true;
    }

    private bool EnterSegmentIfNeeded()
    {
        while (segmentIndex < 0 || segmentTick >= segmentTicks)
        {
            segmentIndex++;
            if (segmentIndex >= segments.Length)
            {
                return false;
            }

            segmentTick = 0;
            segmentTicks = Math.Max(1, (int)Math.Round(segments[segmentIndex].Duration * Options.RateHz));
        }

        return true;
    }

    private void Integrate(SimSegment segment, double progress, bool first)
    {
        SimCar car = Car;
        steerRad = Lerp(segment.SteerStartDeg, segment.SteerEndDeg, progress) * MathUtil.DegToRad;
        double speed = Lerp(segment.SpeedStart, segment.SpeedEnd, progress);
        if (first && segment.Teleport)
        {
            pathRate = 0.0;
            slideAngle = 0.0;
            previousSpeed = speed;
        }

        longitudinalAccel = double.IsNaN(previousSpeed) ? 0.0 : (speed - previousSpeed) / dt;
        previousSpeed = speed;

        if (segment.HoldPath && !holdingPath)
        {
            heldPathRate = pathRate;
        }

        holdingPath = segment.HoldPath;

        // Linear tyre response to the steering, limited by the front axle (understeer).
        double absSpeed = Math.Abs(speed);
        double linearYaw = car.G * speed * steerRad / (1.0 + (car.K * speed * speed));
        double yawCap = absSpeed > MinGripSpeed ? car.FrontGripLimit / absSpeed : double.PositiveInfinity;
        double gripYaw = MathUtil.Clamp(linearYaw, -yawCap, yawCap);
        double target = segment.HoldPath ? heldPathRate : gripYaw;
        pathRate += (target - pathRate) * (1.0 - Math.Exp(-dt / car.TauYaw));

        // Rear grip loss: extra rotation beyond the path turns into body slip, which relaxes when caught.
        double slideCommand = Lerp(segment.SlideYawStart, segment.SlideYawEnd, progress);
        double slideRate = slideCommand - (slideAngle / car.SlideRecoveryTau);
        slideAngle += slideRate * dt;

        TrueYawRate = pathRate + slideRate;
        TrueLateralAccel = speed * pathRate;
        TrueSpeed = speed;
        double beta = (car.BodySlipDegPerAy * MathUtil.DegToRad * TrueLateralAccel) + slideAngle;
        TrueBodySlipDeg = beta * MathUtil.RadToDeg;

        double ayAbs = Math.Abs(TrueLateralAccel);
        double frontExcess = 0.0;
        if (!segment.HoldPath && Math.Abs(linearYaw) > yawCap)
        {
            // Steering beyond the grip limit only adds front slip (road-wheel angle = wheel angle / ratio).
            double limitSteer = yawCap * (1.0 + (car.K * speed * speed)) / (car.G * absSpeed);
            frontExcess = (Math.Abs(steerRad) - limitSteer) / car.SteeringRatio;
        }

        alphaFront = (car.FrontSlipPerAy * ayAbs) + frontExcess;
        alphaRear = (car.RearSlipPerAy * ayAbs) + Math.Abs(slideAngle);
    }

    private void Write(SimSegment segment, double progress, bool first)
    {
        const double g = MathUtil.Gravity;
        VehicleState s = State;
        double beta = TrueBodySlipDeg * MathUtil.DegToRad;
        double forward = TrueSpeed * Math.Cos(beta);
        double lateral = -TrueSpeed * Math.Sin(beta);

        s.Valid = !segment.Invalid;
        s.SimTime = Options.ReportSimTime ? simClock : double.NaN;
        s.WallTime = wallClock;
        s.V = (Options.InvertForward ? -1.0 : 1.0) * (forward + Noise(noise.Speed));
        s.Vy = Options.ReportLateralVelocity ? lateral + Noise(noise.LateralVelocity) : double.NaN;
        double theta = (Car.Theta0Deg * MathUtil.DegToRad) + steerRad + Noise(noise.Steering);
        s.Theta = Options.InvertSteering ? -theta : theta;
        s.ThetaMax = Car.HalfLockDeg * MathUtil.DegToRad;
        s.R = Options.ReportYawRate ? TrueYawRate + KerbSpike(segment) + Noise(noise.YawRate) : double.NaN;
        s.Ay = TrueLateralAccel + Noise(noise.LateralAccel) + (first ? segment.CollisionG * g : 0.0);
        s.Ax = longitudinalAccel + Noise(noise.LongitudinalAccel);
        double restAz = Options.GravityIncluded ? g : 0.0;
        s.Az = (segment.Airborne ? restAz - g : restAz) + Noise(noise.VerticalAccel);
        s.Throttle = Lerp(segment.ThrottleStart, segment.ThrottleEnd, progress);
        s.Brake = Lerp(segment.BrakeStart, segment.BrakeEnd, progress);
        s.Gear = segment.Gear;
        s.Surface = segment.Surface;
        s.OnPitRoad = segment.PitLane;
        s.OnTrack = !segment.NotOnTrack;
        s.InGarage = segment.NotOnTrack;
        s.IsReplay = segment.Replay;
        s.IsSpectating = segment.Spectating;
        s.IsPaused = segment.Paused;
        s.Wetness = segment.Wetness;
        s.ContactCounter = contactCounter;

        bool slipAngles = Options.ReportSlipAngles && segment.SlipAnglesAvailable && Math.Abs(forward) > MinSlipAngleSpeed;
        s.HasSlipAngles = slipAngles;
        s.SlipAnglesInRadians = slipAngles;
        if (slipAngles)
        {
            double sign = TrueLateralAccel >= 0.0 ? 1.0 : -1.0;
            s.Alpha[Wheels.FrontLeft] = sign * (alphaFront + Noise(noise.SlipAngle));
            s.Alpha[Wheels.FrontRight] = sign * (alphaFront + Noise(noise.SlipAngle));
            s.Alpha[Wheels.RearLeft] = sign * (alphaRear + Noise(noise.SlipAngle));
            s.Alpha[Wheels.RearRight] = sign * (alphaRear + Noise(noise.SlipAngle));
            s.AlphaFront = (Math.Abs(s.Alpha[Wheels.FrontLeft]) + Math.Abs(s.Alpha[Wheels.FrontRight])) / 2.0;
            s.AlphaRear = (Math.Abs(s.Alpha[Wheels.RearLeft]) + Math.Abs(s.Alpha[Wheels.RearRight])) / 2.0;
        }
        else
        {
            s.AlphaFront = double.NaN;
            s.AlphaRear = double.NaN;
            for (int i = 0; i < Wheels.Count; i++)
            {
                s.Alpha[i] = double.NaN;
            }
        }
    }

    private double KerbSpike(SimSegment segment)
    {
        if (!(segment.KerbSpikeInterval > 0.0))
        {
            return 0.0;
        }

        int every = Math.Max(3, (int)Math.Round(segment.KerbSpikeInterval * Options.RateHz));
        if (segmentTick % every != 0)
        {
            return 0.0;
        }

        kerbSign = -kerbSign;
        return kerbSign * segment.KerbSpikeAmplitude;
    }

    /// <summary>Gaussian sample (Box-Muller); 0 when <paramref name="sigma"/> is 0 so noise-free runs stay exact.</summary>
    private double Noise(double sigma)
    {
        if (!(sigma > 0.0))
        {
            return 0.0;
        }

        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
