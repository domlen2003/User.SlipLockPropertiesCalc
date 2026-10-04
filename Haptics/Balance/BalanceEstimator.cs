using System;
using DivebombLogistics.Core;
using DivebombLogistics.Haptics.Settings;

namespace DivebombLogistics.Haptics.Balance;

/// <summary>
/// Understeer/oversteer estimator (spec sections 2, 3 and 6). Compares the yaw rate the driver asks for (in-game
/// steering through a lagged bicycle model) with the yaw rate the car delivers, adds countersteer and body-slip
/// detectors and, where the sim reports tyre slip angles, a direct front/rear slip-angle comparison. Produces two
/// shaped 0..1 outputs plus context tags and debug values in <see cref="Outputs"/>, and feeds the per-car
/// <see cref="BalanceLearner"/>.
/// </summary>
/// <remarks>
/// <para>Runs on the SimHub data thread. <see cref="Update"/> is allocation-free and never throws for any input
/// (the only allocations are one-off log messages when a sign convention or the gravity convention is decided).</para>
/// <para>Time base: sim time when the source provides it (so duplicate and frozen frames are recognised), else wall
/// time. Every tick is classified by a <see cref="BalanceGate"/>: hard gates (no data, frozen, replay, not on track,
/// paused) skip the pipeline entirely; soft gates (pit lane, reverse, low speed, blanks, spin timeout) keep the
/// filters running but drive the outputs to 0 and stop learning. <see cref="BalanceGate.Calibrating"/> is partial:
/// until the sim's steering sign is verified, only body slip and spin reach the outputs (see
/// <see cref="CheckSteeringSign"/>).</para>
/// <para>The loaded <see cref="CarProfile"/> is read live every tick (sensitivities, overrides, learning lock); the
/// shell mutates it only on the data thread.</para>
/// </remarks>
internal sealed class BalanceEstimator
{
    // ---- Timing ----

    /// <summary>A sample gap longer than this (s) is a rejoin/teleport: outputs are blanked, not just filters reset.</summary>
    private const double LongGapSeconds = 1.0;

    /// <summary>
    /// A forward-speed change larger than this within one sample (m/s; ≈ 60 g at 60 Hz) is a teleport (reset to
    /// pits, tow) or a crash, even when the sim clock runs on continuously.
    /// </summary>
    private const double TeleportSpeedJump = 10.0;

    /// <summary>Timestamps closer than this (s) are the same sample (tolerates rounding in the sim's clock).</summary>
    private const double MinSampleStep = 1e-4;

    /// <summary>Largest wall-clock step (s) used to decay outputs while no samples are processed.</summary>
    private const double MaxIdleStep = 1.0;

    /// <summary>Effective parameters are re-resolved at least this often (s, wall clock) to pick up tuning edits.</summary>
    private const double ResolveInterval = 0.5;

    // ---- Sign and gravity conventions ----
    private const int ForwardSignVotes = 100;
    private const int SteeringSignVotes = 200;

    /// <summary>|mean vote| needed to decide a sign convention (0.5 = at least 75 % of the votes agree).</summary>
    private const double SignDecisionThreshold = 0.5;

    private const double ForwardCheckMinThrottle = 0.3;
    private const double ForwardCheckMinSpeed = 5.0;

    /// <summary>Forward-sign votes are skipped while |Vy| exceeds this fraction of |V| (the car is sliding sideways).</summary>
    private const double ForwardCheckMaxSideSlip = 0.5;

    // Steering-sign votes come from clean cornering at any lateral level (sign(θ·r) > 0 holds up to the grip
    // limit), not only from the linear learning region: otherwise ovals and fast circuits never verify the sign.
    // Slides are excluded, because a countersteering driver votes against the true sign on every tick.
    private const double SteeringCheckMinSpeed = 10.0;
    private const double SteeringCheckMinSteerDeg = 3.0;
    private const double SteeringCheckMinYawRate = 0.08;
    private const double SteeringCheckMaxSteerRate = 0.5;
    private const double SteeringCheckMaxYawAccel = 0.3;
    private const double SteeringCheckMaxBrake = 0.2;

    /// <summary>Votes need |β| below this (deg) when the sim reports the lateral velocity.</summary>
    private const double SteeringCheckMaxBodySlipDeg = 3.0;

    /// <summary>
    /// After verification a slow monitor keeps counting clean samples (|β| below
    /// <see cref="SteeringMonitorMaxBodySlipDeg"/>); when at least 90 % of <see cref="SteeringMonitorVotes"/>
    /// disagree with the verified sign, the verification is reopened.
    /// </summary>
    private const int SteeringMonitorVotes = 600;

    private const double SteeringMonitorThreshold = 0.8;
    private const double SteeringMonitorMaxBodySlipDeg = 2.0;

    /// <summary>Forward speed (m/s) below which the car counts as rolling backwards.</summary>
    private const double ReverseSpeed = -0.5;

    private const int GravitySamplesNeeded = 60;
    private const double GravityMeanTau = 5.0;
    private const double GravityMaxLateral = 3.0;
    private const double GravityMinSpeed = 5.0;

    // ---- Yaw-rate fallback ----

    /// <summary>Minimum speed (m/s) for estimating the yaw rate as ay / v when the sim has no yaw rate.</summary>
    private const double LateralYawMinSpeed = 5.0;

    /// <summary>Confidence factor for the ay / v yaw-rate fallback (invalid on banked tracks).</summary>
    private const double LateralYawConfidenceFactor = 0.5;

    // ---- Detectors ----

    /// <summary>
    /// The yaw-ratio detectors fade in over |r_ref| ∈ [RFloor, RFloor·(1 + this)] (0.05..0.15 rad/s by default).
    /// Near the floor both r and r_ref are small, so the relative error from a few milliseconds of lag mismatch on
    /// turn-in, unwind or a direction change (≈ dr/dt · Δτ / |r|) would otherwise read as a large yaw deficit/excess.
    /// </summary>
    private const double YawRatioFadeWidth = 2.0;

    private const double MinYawRatioFade = 1e-3;

    // ---- Tags ----
    private const double PowerThrottle = 0.5;
    private const double LiftThrottle = 0.2;
    private const double TagBrake = 0.1;
    private const double ExitThrottle = 0.3;

    // ---- Confidence by G source (spec: default/preset values are heuristics) ----
    private const double ManualConfidence = 1.0;
    private const double SessionConfidence = 0.7;
    private const double PresetConfidence = 0.3;
    private const double DefaultConfidence = 0.2;

    // ---- Learning gates (spec 5.2 / DESIGN 6.2 step 17) ----
    private const double MinOnTrackForLearning = 2.0;
    private const double QuietTimeAfterEvent = 1.0;
    private const double LateralSampleMinSpeed = 10.0;
    private const double StraightMaxLateral = 0.5;
    private const double StraightMinSpeed = 20.0;
    private const double StraightMaxYawRate = 0.03;
    private const double StraightMaxSteerRate = 0.2;
    private const double ModelMinSpeed = 10.0;
    private const double ModelMaxLateral = 4.0;
    private const double ModelMaxLateralFraction = 0.35;
    private const double ModelMaxSteerRate = 0.5;
    private const double ModelMaxYawAccel = 0.3;
    private const double ModelMinSteerDeg = 2.0;
    private const double ModelMaxBrake = 0.2;

    /// <summary>
    /// Model samples also require the lagged reference to have settled (|r_ss - r_ref| within this fraction of
    /// |r_ss|): right after a steering change the measured yaw rate still lags, which would bias G.
    /// </summary>
    private const double ModelSettledTolerance = 0.05;

    private const double BodySlipMinSpeed = 10.0;
    private const double BodySlipMaxOversteer = 0.3;
    private const double SlipAngleMinSpeed = 15.0;
    private const double SlipAngleMinLateralFraction = 0.7;

    private static readonly BalanceOverrides NoOverrides = new BalanceOverrides();

    private readonly BalanceTuning tuning;
    private readonly ILog log;
    private readonly BalanceLearner learner;
    private readonly ParamResolver resolver;
    private readonly EffectiveParams parameters = new EffectiveParams();

    /// <summary>Copy of the overrides at the last resolution (detects UI edits without allocating).</summary>
    private readonly BalanceOverrides resolvedOverrides = new BalanceOverrides();

    // ---- Input filters ----
    private readonly Median3 yawMedian = new Median3();
    private readonly Ema yawEma = new Ema();
    private readonly Ema steeringEma = new Ema();
    private readonly Ema lateralVelocityEma = new Ema();
    private readonly Ema forwardSpeedEma = new Ema();
    private readonly Ema alphaFrontEma = new Ema();
    private readonly Ema alphaRearEma = new Ema();

    // Single-sample vertical spikes (kerbs, bumps) must not trigger the airborne blank.
    private readonly Median3 verticalMedian = new Median3();
    private readonly Ema verticalMean = new Ema();

    // ---- Output shaping ----
    private readonly HysteresisGate understeerHysteresis = new HysteresisGate();
    private readonly HysteresisGate oversteerHysteresis = new HysteresisGate();
    private readonly AttackRelease understeerEnvelope = new AttackRelease();
    private readonly AttackRelease oversteerEnvelope = new AttackRelease();

    // ---- Runtime sign verification ----
    private readonly SignVote forwardVote = new SignVote(ForwardSignVotes, SignDecisionThreshold);
    private readonly SignVote steeringVote = new SignVote(SteeringSignVotes, SignDecisionThreshold);
    private readonly SignVote steeringMonitor = new SignVote(SteeringMonitorVotes, SteeringMonitorThreshold);

    // ---- Loaded car ----
    private CarProfile profile;
    private SimCalibration calibration = new SimCalibration();
    private string carClass = string.Empty;

    // ---- Timeline and gate state ----
    private bool initialized;
    private bool filtersStale;
    private bool wasOffTrack;
    private double lastTime;
    private double lastWallTime = double.NaN;
    private double lastNewSampleWallTime;
    private double lastForwardSpeed = double.NaN;
    private int lastContactCounter;
    private double blankUntil = double.NegativeInfinity;
    private double contactUntil = double.NegativeInfinity;
    private double airborneUntil = double.NegativeInfinity;
    private double lastContactTime = double.NegativeInfinity;
    private double lastAirborneTime = double.NegativeInfinity;
    private double onTrackSince;
    private double spinSince = double.NaN;
    private bool yawTraceActive;

    // ---- Gravity convention detection ----
    private int gravitySamples;
    private bool gravityConventionUnknown;

    // ---- Effective parameters (sanitized copies of the resolver output) ----
    private bool resolveRequested = true;
    private int resolvedLearnerVersion;
    private double lastResolveWallTime = double.NegativeInfinity;
    private double thetaMax = double.NaN;
    private double gainG;
    private double understeerK;
    private double theta0;
    private double tauYaw;
    private double betaOnsetDeg;
    private double betaFullDeg;
    private bool slipAnglesInRadians;

    // ---- Current-sample signals (valid after ReadInputs; published as debug values) ----
    private double yawRate = double.NaN;
    private bool yawFromLateral;
    private double steering = double.NaN;
    private double steeringEff = double.NaN;
    private double forwardSpeed;
    private double lateralVelocity = double.NaN;
    private double planarSpeed;
    private double lateralAbs = double.NaN;
    private double lateralNorm;
    private double steerRate = double.NaN;
    private double yawAccel = double.NaN;
    private double alphaFront = double.NaN;
    private double alphaRear = double.NaN;
    private double yawSteady = double.NaN;
    private double yawRef = double.NaN;
    private double yawRatio = double.NaN;
    private double bodySlipDeg = double.NaN;
    private double speedRamp;
    private bool modelReady;
    private bool directReady;
    private bool spinning;

    /// <summary>The sim's steering sign is not verified yet: model-path outputs and countersteer are suppressed.</summary>
    private bool steeringCalibrating;

    // ---- Current-sample detector intensities (0..1, before gates and shaping) ----
    private double usModel;
    private double osYaw;
    private double osCountersteer;
    private double osBodySlip;
    private double osModel;
    private double usDirect;
    private double osDirect;

    // Yaw excess and countersteer before the calibration suppression: they still keep suspected slides out of the
    // body-slip envelope while the sign is being verified.
    private double osYawRaw;
    private double osCountersteerRaw;

    /// <param name="tuning">Live global tuning (<c>HapticsSettings.Balance</c>); read every tick.</param>
    /// <param name="log">Logger for rare calibration events (null = none).</param>
    public BalanceEstimator(BalanceTuning tuning, ILog log)
    {
        this.tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        this.log = log ?? NullLog.Instance;
        learner = new BalanceLearner(tuning, this.log);
        resolver = new ParamResolver(tuning);
        ApplySanitizedParameters();
    }

    /// <summary>Outputs of the last tick, updated in place (read concurrently by the property exporter).</summary>
    public BalanceOutputs Outputs { get; } = new BalanceOutputs();

    /// <summary>True when the learned state changed since the last <see cref="SaveTo"/>.</summary>
    public bool LearnerDirty => profile != null && learner.Dirty;

    /// <summary>Class preset auto-detected for the loaded car (shown as "Auto (…)" next to the manual choice).</summary>
    public BalanceClassPreset AutoClassPreset => profile != null ? parameters.AutoClassPreset : BalanceClassPreset.None;

    /// <summary>True when the sim calibration passed to <see cref="LoadCar"/> was updated (shell persists settings).</summary>
    public bool CalibrationChanged { get; private set; }

    /// <summary>Acknowledges <see cref="CalibrationChanged"/> once the shell has scheduled the settings save.</summary>
    public void ClearCalibrationChanged() => CalibrationChanged = false;

    /// <summary>
    /// Attaches a car: loads its learned model from <c>profile.Learned</c>, applies its learning lock, resets all
    /// filters and blanks the outputs for <see cref="BalanceTuning.ResetBlankTime"/>. Learning of the previous car
    /// that was not written back with <see cref="SaveTo"/> is discarded. <paramref name="calibration"/> is the live
    /// per-sim calibration object that the estimator updates when it verifies sign or gravity conventions.
    /// A null profile unloads.
    /// </summary>
    public void LoadCar(CarProfile profile, SimCalibration calibration, string carClass)
    {
        if (profile == null)
        {
            Unload();
            return;
        }

        this.profile = profile;
        this.calibration = calibration ?? new SimCalibration();
        this.carClass = carClass ?? string.Empty;
        learner.Load(profile.Learned); // also restarts the session adaptation layer
        learner.Locked = profile.LearningLocked;
        ResetRuntimeState();
        ResolveParameters(double.NegativeInfinity);
        ClearOutputs();
    }

    /// <summary>Detaches the car (no car known): outputs go to 0 (gate NoData) and nothing is learned.</summary>
    public void Unload()
    {
        profile = null;
        ResetRuntimeState();
        ClearOutputs();
    }

    /// <summary>
    /// Resets filters, timers and the session adaptation layer (game change, session change, retest) and blanks
    /// the outputs; the loaded car and its learned baseline stay.
    /// </summary>
    public void Reset()
    {
        ResetRuntimeState();
        if (profile != null)
        {
            learner.ResetSession();
            ResolveParameters(double.NegativeInfinity);
        }

        ClearOutputs();
    }

    /// <summary>Writes the learned state into <paramref name="target"/> (the loaded car's profile); clears <see cref="LearnerDirty"/>.</summary>
    public void SaveTo(CarProfile target)
    {
        if (profile == null || target == null)
        {
            return;
        }

        target.Learned ??= new BalanceLearnedState();
        learner.SaveTo(target.Learned);
    }

    /// <summary>Forgets everything learned for the loaded car (baseline and session layer).</summary>
    public void ResetLearning()
    {
        if (profile == null)
        {
            return;
        }

        learner.ResetBaseline(); // baseline and session layer
        AbortYawTrace();
        ResolveParameters(double.NegativeInfinity);
        PublishParameters();
    }

    /// <summary>
    /// Re-verifies the steering and forward sign conventions of the current sim (they are shared by all cars of the
    /// sim) with the next clean corners. The sign values are kept: they are what the learned steering offsets were
    /// measured with, and a vote that finds a sign inverted corrects the loaded car's offset accordingly. Until the
    /// steering sign is verified again the model path is suppressed (<see cref="BalanceGate.Calibrating"/>).
    /// The gravity convention is kept (it is decided robustly from the mean vertical acceleration). No-op without a car.
    /// </summary>
    public void ResetCalibration()
    {
        if (profile == null)
        {
            return; // the calibration object belongs to the loaded car's sim
        }

        calibration.SteeringSignVerified = false;
        calibration.ForwardSignVerified = false;
        forwardVote.Reset();
        steeringVote.Reset();
        steeringMonitor.Reset();
        AbortYawTrace();
        CalibrationChanged = true;
        PublishParameters();
        log.Info("Balance: steering and forward sign will be verified again.");
    }

    /// <summary>Processes one telemetry tick. Allocation-free; never throws.</summary>
    public void Update(VehicleState s)
    {
        double wallStep = AdvanceWallClock(s);
        if (profile == null || s == null)
        {
            Idle(BalanceGate.NoData, wallStep);
            return;
        }

        double time = MathUtil.IsFinite(s.SimTime) ? s.SimTime : s.WallTime;
        if (!s.Valid || !MathUtil.IsFinite(time))
        {
            Idle(BalanceGate.NoData, wallStep);
            return;
        }

        if (learner.Locked != profile.LearningLocked)
        {
            learner.Locked = profile.LearningLocked;
        }

        if (!initialized)
        {
            StartTimeline(s, time);
            return;
        }

        double dt = time - lastTime;
        if (Math.Abs(dt) < MinSampleStep)
        {
            // Duplicate frame (sim telemetry slower than SimHub, or the sim is paused): hold the outputs and only
            // decay them once no new sample arrived for FrozenTimeout.
            if (s.WallTime - lastNewSampleWallTime > tuning.FrozenTimeout)
            {
                Idle(BalanceGate.Frozen, wallStep);
            }

            return;
        }

        bool teleported = IsTeleport(s);
        if (dt < 0.0 || dt > tuning.MaxDt || teleported)
        {
            // Time running backwards means a session restart or replay rewind: without restarting the timeline
            // every later sample would look like a duplicate of the old, larger timestamp.
            HandleDiscontinuity(s, time, dt < 0.0 || dt > LongGapSeconds || teleported);
            return;
        }

        lastTime = time;
        lastNewSampleWallTime = s.WallTime;

        BalanceGate hardGate = EvaluateHardGates(s);
        if (hardGate != BalanceGate.Active)
        {
            wasOffTrack |= hardGate == BalanceGate.NotOnTrack;
            filtersStale = true;
            Idle(hardGate, wallStep);
            return;
        }

        if (wasOffTrack)
        {
            // Back on track after the garage, a tow or AI control: the car may have been moved.
            wasOffTrack = false;
            RestartTimers(time);
            blankUntil = time + tuning.ResetBlankTime;
            filtersStale = true;
        }

        if (filtersStale)
        {
            ResetFilters();
            filtersStale = false;
        }

        Process(s, time, dt);
    }

    // =====================================================================================================
    // Per-sample pipeline
    // =====================================================================================================

    private void Process(VehicleState s, double time, double dt)
    {
        VerifyForwardSign(s);
        double v = s.V * calibration.ForwardSign;
        lastForwardSpeed = v;
        if (MathUtil.IsFinite(s.ThetaMax) && s.ThetaMax > 0.0)
        {
            thetaMax = s.ThetaMax;
        }

        if (s.HasSlipAngles)
        {
            slipAnglesInRadians = s.SlipAnglesInRadians;
        }

        steeringCalibrating = !calibration.SteeringSignVerified;
        ReadInputs(s, v, dt);
        DetectVerticalAndContactEvents(s, time, dt);
        if (IsResolveDue(s.WallTime))
        {
            ResolveParameters(s.WallTime);
        }

        RunVehicleModel(dt);
        speedRamp = SpeedRamp(planarSpeed);
        EvaluateDetectors(s);

        BalancePath path = SelectPath();
        CombineDetectors(path, out double understeer, out double oversteer);

        spinning = MathUtil.IsFinite(bodySlipDeg) && Math.Abs(bodySlipDeg) > tuning.BetaSpinDeg && speedRamp > 0.0;
        bool spinTimedOut = false;
        if (spinning)
        {
            if (double.IsNaN(spinSince))
            {
                spinSince = time;
            }

            spinTimedOut = time - spinSince > tuning.SpinTimeout;
            understeer = 0.0;
            oversteer = 1.0;
        }
        else
        {
            spinSince = double.NaN;
        }

        // A spinning car travels sideways or backwards relative to its heading; that is not reversing.
        bool reverse = s.Gear < 0 || (v < ReverseSpeed && !spinning);
        BalanceGate gate = EvaluateSoftGates(s, time, reverse, path, spinTimedOut);

        // Calibrating still passes what survived the suppression (body slip, spin); it is just not "Active".
        bool open = gate == BalanceGate.Active || gate == BalanceGate.Calibrating;

        // Shaping (spec 3): onset hysteresis, speed ramp, attack/release envelope.
        double usTarget = understeerHysteresis.Apply(open ? understeer : 0.0, tuning.Hysteresis) * speedRamp;
        double osTarget = oversteerHysteresis.Apply(open ? oversteer : 0.0, tuning.Hysteresis) * speedRamp;
        double os = MathUtil.Clamp01(oversteerEnvelope.Update(osTarget, dt, tuning.Attack, tuning.Release));
        double us = MathUtil.Clamp01(understeerEnvelope.Update(usTarget, dt, tuning.Attack, tuning.Release));
        if (os > tuning.MutualExclusionOs && us > 0.0)
        {
            // Mutual exclusion also on the shaped outputs: the understeer release tail must not overlap a slide.
            understeerEnvelope.Reset();
            understeerHysteresis.Reset();
            us = 0.0;
        }

        bool learnGate = IsLearningAllowed(s, time, reverse);
        bool learned = learnGate && FeedLearner(s, time, dt);
        if (!learnGate)
        {
            AbortYawTrace();
        }

        double tag = tuning.TagThreshold;
        Outputs.Understeer = us;
        Outputs.Oversteer = os;
        Outputs.PowerOversteer = open && os > tag && s.Throttle > PowerThrottle;
        Outputs.LiftOrBrakeOversteer = open && os > tag && (s.Throttle < LiftThrottle || s.Brake > TagBrake);
        Outputs.EntryUndersteer = open && us > tag && s.Brake > TagBrake;
        Outputs.ExitUndersteer = open && us > tag && s.Throttle > ExitThrottle;
        Outputs.Countersteer = open && osCountersteer > 0.0;
        Outputs.Spin = open && spinning;
        Outputs.Active = gate == BalanceGate.Active;
        Outputs.Confidence = Confidence(path);
        Outputs.Gate = gate;
        Outputs.Path = path;
        Outputs.LearningActive = learned;
        PublishSignals();
        PublishParameters();
    }

    /// <summary>Filters the raw inputs (median against spikes, EMA against noise) and derives rates.</summary>
    private void ReadInputs(VehicleState s, double v, double dt)
    {
        double alpha = MathUtil.LagAlpha(dt, tuning.TauInput);

        double rawYaw = s.R;
        yawFromLateral = false;
        if (!MathUtil.IsFinite(rawYaw))
        {
            // Spec fallback r ≈ ay / v: flat-road kinematics, wrong on banking, so it is flagged as low confidence
            // and never used to learn the vehicle model.
            rawYaw = double.NaN;
            if (MathUtil.IsFinite(s.Ay) && v > LateralYawMinSpeed)
            {
                rawYaw = s.Ay / v;
                yawFromLateral = true;
            }
        }

        double previousSteering = steering;
        double previousYaw = yawRate;
        yawRate = MedianThenEma(yawMedian, yawEma, rawYaw, alpha);
        steering = EmaOrReset(steeringEma, s.Theta * calibration.SteeringSign, alpha);
        lateralVelocity = EmaOrReset(lateralVelocityEma, s.Vy, alpha);
        forwardSpeed = MathUtil.FiniteOr(EmaOrReset(forwardSpeedEma, v, alpha), 0.0);
        steerRate = (steering - previousSteering) / dt;
        yawAccel = (yawRate - previousYaw) / dt;
        planarSpeed = MathUtil.IsFinite(lateralVelocity)
            ? Math.Sqrt((forwardSpeed * forwardSpeed) + (lateralVelocity * lateralVelocity))
            : Math.Abs(forwardSpeed);
        lateralAbs = MathUtil.IsFinite(s.Ay) ? Math.Abs(s.Ay) : double.NaN;

        if (s.HasSlipAngles)
        {
            alphaFront = EmaOrReset(alphaFrontEma, s.AlphaFront, alpha);
            alphaRear = EmaOrReset(alphaRearEma, s.AlphaRear, alpha);
        }
        else
        {
            alphaFrontEma.Reset();
            alphaRearEma.Reset();
            alphaFront = double.NaN;
            alphaRear = double.NaN;
        }
    }

    /// <summary>Gravity convention detection, airborne detection (spec 4.1) and contact detection.</summary>
    private void DetectVerticalAndContactEvents(VehicleState s, double time, double dt)
    {
        const double g = MathUtil.Gravity;
        if (!calibration.GravityIncluded.HasValue && !gravityConventionUnknown && MathUtil.IsFinite(s.Az)
            && lateralAbs < GravityMaxLateral && forwardSpeed > GravityMinSpeed)
        {
            verticalMean.Update(s.Az, MathUtil.LagAlpha(dt, GravityMeanTau));
            if (++gravitySamples >= GravitySamplesNeeded)
            {
                DecideGravityConvention(verticalMean.Value);
            }
        }

        double az = double.NaN;
        if (MathUtil.IsFinite(s.Az))
        {
            az = verticalMedian.Update(s.Az);
        }
        else
        {
            verticalMedian.Reset();
        }

        if (MathUtil.IsFinite(az) && !gravityConventionUnknown)
        {
            // Until detected, a reading near +1 g means "gravity included" (the DESIGN heuristic).
            bool included = calibration.GravityIncluded ?? az > g / 2.0;
            double specificForce = included ? az : az + g;
            if (specificForce < (1.0 - tuning.AirborneMarginG) * g)
            {
                airborneUntil = time + tuning.AirborneHold;
                lastAirborneTime = time;
            }
        }

        double ax = MathUtil.FiniteOr(s.Ax, 0.0);
        double ay = MathUtil.FiniteOr(s.Ay, 0.0);
        double contactLimit = tuning.ContactBlankG * g;
        if ((ax * ax) + (ay * ay) > contactLimit * contactLimit || s.ContactCounter != lastContactCounter)
        {
            contactUntil = time + tuning.ContactBlankTime;
            lastContactTime = time;
        }

        lastContactCounter = s.ContactCounter;
    }

    /// <summary>Lagged bicycle model (spec 2): r_ss, r_ref (uncapped), yaw ratio and body slip.</summary>
    private void RunVehicleModel(double dt)
    {
        if (MathUtil.IsFinite(steering))
        {
            steeringEff = steering - theta0;
            yawSteady = gainG * forwardSpeed * steeringEff / (1.0 + (understeerK * forwardSpeed * forwardSpeed));

            // r_ref is deliberately not capped at the grip limit: asking for more yaw than the car delivers IS understeer.
            yawRef = MathUtil.IsFinite(yawRef)
                ? yawRef + ((yawSteady - yawRef) * MathUtil.LagAlpha(dt, tauYaw))
                : yawSteady;
        }
        else
        {
            steeringEff = double.NaN;
            yawSteady = double.NaN;
            yawRef = double.NaN;
        }

        modelReady = MathUtil.IsFinite(yawRef) && MathUtil.IsFinite(yawRate);
        yawRatio = modelReady && Math.Abs(yawRef) > tuning.RFloor ? yawRate / yawRef : double.NaN;
        bodySlipDeg = MathUtil.IsFinite(lateralVelocity)
            ? Math.Atan2(lateralVelocity, Math.Abs(forwardSpeed)) * MathUtil.RadToDeg
            : double.NaN;
    }

    /// <summary>Raw detector intensities (spec 3 and 9) with the per-car sensitivities as gains on the metrics.</summary>
    private void EvaluateDetectors(VehicleState s)
    {
        double usGain = CarProfile.ClampSensitivity(profile.UndersteerSensitivity) / 100.0;
        double osGain = CarProfile.ClampSensitivity(profile.OversteerSensitivity) / 100.0;
        double loose = s.Surface == SurfaceKind.Loose ? tuning.LooseSurfaceMultiplier : 1.0;
        double gamma = tuning.Gamma;
        double rFloor = tuning.RFloor;

        double fade = MathUtil.IsFinite(yawRef)
            ? MathUtil.Clamp01((Math.Abs(yawRef) - rFloor) / Math.Max(rFloor * YawRatioFadeWidth, MinYawRatioFade))
            : 0.0;

        // Understeer: yaw deficit, only for 0 <= rho < 1 (rho < 0 is countersteer, i.e. oversteer).
        usModel = MathUtil.IsFinite(yawRatio) && yawRatio >= 0.0 && yawRatio < 1.0
            && Math.Abs(steeringEff) > tuning.ThetaDeadbandDeg * MathUtil.DegToRad
            ? MathUtil.Map((1.0 - yawRatio) * usGain, tuning.UsOnset, tuning.UsFull, gamma) * fade
            : 0.0;

        // Oversteer 1: yaw excess.
        osYaw = yawRatio > 1.0
            ? MathUtil.Map((yawRatio - 1.0) * osGain, tuning.OsYawOnset * loose, tuning.OsYawFull * loose, gamma) * fade
            : 0.0;

        // Oversteer 2: countersteer. Beyond the spec's steering-vs-yaw sign test, the lagged request r_ref must
        // oppose the yaw rate too: in a quick direction change the wheel crosses centre about one yaw lag before
        // the car does, which the plain test would report as a countersteer on every chicane.
        bool countersteer = modelReady
            && Math.Abs(steeringEff) > tuning.CsThetaMinDeg * MathUtil.DegToRad
            && Math.Abs(yawRate) > tuning.CsROnset
            && MathUtil.Sign(steeringEff) != MathUtil.Sign(yawRate)
            && yawRef * yawRate < 0.0;
        osCountersteer = countersteer
            ? Math.Max(tuning.CsBase, MathUtil.Map(Math.Abs(yawRate) * osGain, tuning.CsROnset, tuning.CsRFull, gamma))
            : 0.0;

        // Oversteer 3: body slip beyond the car's learned normal envelope at the current lateral load.
        lateralNorm = parameters.AyMaxLearned && parameters.AyMax > 0.0 && MathUtil.IsFinite(lateralAbs)
            ? MathUtil.Clamp01(lateralAbs / parameters.AyMax)
            : 0.0;
        if (MathUtil.IsFinite(bodySlipDeg))
        {
            double envelope = Math.Max(0.0, MathUtil.FiniteOr(learner.BetaEnvelopeDeg(lateralNorm), 0.0));
            osBodySlip = MathUtil.Map((Math.Abs(bodySlipDeg) - envelope) * osGain, betaOnsetDeg * loose, betaFullDeg * loose, gamma);
        }
        else
        {
            osBodySlip = 0.0;
        }

        osYawRaw = osYaw;
        osCountersteerRaw = osCountersteer;
        if (steeringCalibrating)
        {
            // Owner decision: never false outputs on the first drive in a sim. With an unverified (possibly
            // inverted) steering sign the yaw ratio is negative in every corner and the countersteer detector would
            // fire in all of them, so everything that depends on the steering direction is held at 0.
            usModel = 0.0;
            osYaw = 0.0;
            osCountersteer = 0.0;
        }

        osModel = Math.Max(osYaw, Math.Max(osCountersteer, osBodySlip));

        // Direct path: front vs rear slip angle, normalized by the learned peak (unit-agnostic).
        directReady = tuning.Mode != BalanceMode.ModelOnly && s.HasSlipAngles
            && MathUtil.IsFinite(alphaFront) && MathUtil.IsFinite(alphaRear)
            && parameters.AlphaPeakLearned && parameters.AlphaPeak > 0.0;
        if (directReady)
        {
            double peak = parameters.AlphaPeak;
            usDirect = alphaFront >= tuning.DirectPeakGate * peak
                ? MathUtil.Map((alphaFront - alphaRear) / peak * usGain, tuning.DirectUsOnset, tuning.DirectUsFull, gamma)
                : 0.0;
            osDirect = MathUtil.Map((alphaRear - alphaFront) / peak * osGain, tuning.DirectOsOnset * loose, tuning.DirectOsFull * loose, gamma);
        }
        else
        {
            usDirect = 0.0;
            osDirect = 0.0;
        }
    }

    private BalancePath SelectPath()
    {
        switch (tuning.Mode)
        {
            case BalanceMode.ModelOnly:
                return modelReady ? BalancePath.Model : BalancePath.None;
            case BalanceMode.DirectOnly:
                return directReady ? BalancePath.Direct : BalancePath.None;
            default:
                if (directReady)
                {
                    return BalancePath.Direct;
                }

                return modelReady ? BalancePath.Model : BalancePath.None;
        }
    }

    private void CombineDetectors(BalancePath path, out double understeer, out double oversteer)
    {
        switch (path)
        {
            case BalancePath.Direct:
                understeer = usDirect;
                if (tuning.Mode == BalanceMode.DirectOnly)
                {
                    oversteer = osDirect;
                }
                else
                {
                    // The model's yaw excess is trusted only with a calibrated gain; countersteer and body slip
                    // do not depend on it.
                    double yaw = IsModelCalibrated() ? osYaw : 0.0;
                    oversteer = Math.Max(Math.Max(osDirect, osCountersteer), Math.Max(osBodySlip, yaw));
                }

                break;
            case BalancePath.Model:
                understeer = usModel;
                oversteer = osModel;
                break;
            default:
                understeer = 0.0;
                oversteer = 0.0;
                break;
        }

        if (oversteer > tuning.MutualExclusionOs)
        {
            understeer = 0.0;
        }
    }

    private BalanceGate EvaluateSoftGates(VehicleState s, double time, bool reverse, BalancePath path, bool spinTimedOut)
    {
        if (s.OnPitRoad)
        {
            return BalanceGate.PitLane;
        }

        if (reverse)
        {
            return BalanceGate.Reverse;
        }

        if (speedRamp <= 0.0)
        {
            return BalanceGate.LowSpeed;
        }

        if (time < blankUntil)
        {
            return BalanceGate.Blanked;
        }

        if (time < contactUntil)
        {
            return BalanceGate.Contact;
        }

        if (time < airborneUntil)
        {
            return BalanceGate.Airborne;
        }

        if (path == BalancePath.None)
        {
            // The signals the selected mode needs are missing (no steering/yaw, or DirectOnly without slip angles).
            return BalanceGate.NoData;
        }

        if (spinTimedOut)
        {
            return BalanceGate.SpinTimeout;
        }

        return steeringCalibrating && path == BalancePath.Model ? BalanceGate.Calibrating : BalanceGate.Active;
    }

    private double Confidence(BalancePath path)
    {
        double model = 0.0;
        if (modelReady && !steeringCalibrating)
        {
            switch (parameters.GSource)
            {
                case ParamSource.Manual:
                    model = ManualConfidence;
                    break;
                case ParamSource.Learned:
                    model = MathUtil.Clamp01(parameters.ConfG);
                    break;
                case ParamSource.Session:
                    model = SessionConfidence;
                    break;
                case ParamSource.Preset:
                    model = PresetConfidence;
                    break;
                default:
                    model = DefaultConfidence;
                    break;
            }

            if (yawFromLateral)
            {
                model *= LateralYawConfidenceFactor;
            }
        }

        switch (path)
        {
            case BalancePath.Direct:
                return Math.Max(model, MathUtil.Clamp01(parameters.ConfAlphaPeak));
            case BalancePath.Model:
                return model;
            default:
                return 0.0;
        }
    }

    // =====================================================================================================
    // Learning (spec 5.2, DESIGN 6.2 step 17)
    // =====================================================================================================

    private bool IsLearningAllowed(VehicleState s, double time, bool reverse)
    {
        // Wet is excluded like loose surfaces and kerbs: the persisted baseline describes the car on dry asphalt.
        // Known deviation from spec 6 ("ay_max adapts in the wet"): there is no session-only ay_max yet, so in the
        // rain ay_max, the body-slip normalization and the lateral learning gate keep their dry values.
        bool surfaceOk = s.Surface == SurfaceKind.Asphalt || s.Surface == SurfaceKind.Unknown;
        return surfaceOk
            && !s.OnPitRoad
            && !reverse
            && !spinning
            && time - onTrackSince > MinOnTrackForLearning
            && time >= blankUntil
            && time - lastContactTime > QuietTimeAfterEvent
            && time - lastAirborneTime > QuietTimeAfterEvent
            && !(s.Wetness >= tuning.WetnessLearningMax);
    }

    /// <summary>Feeds every learner channel whose gate is open this tick; returns true if any sample was added.</summary>
    private bool FeedLearner(VehicleState s, double time, double dt)
    {
        bool added = false;
        bool steeringKnown = MathUtil.IsFinite(steering);
        bool yawKnown = MathUtil.IsFinite(yawRate);

        // The vehicle model and steering offset are learned only from the sim's own yaw rate and only with a
        // verified steering sign: learning with an inverted sign would store an offset of the wrong sign.
        bool modelInputsOk = steeringKnown && yawKnown && !yawFromLateral && calibration.SteeringSignVerified;

        if (forwardSpeed > LateralSampleMinSpeed && MathUtil.IsFinite(lateralAbs))
        {
            learner.AddLateralSample(lateralAbs);
            added = true;
        }

        if (modelInputsOk && lateralAbs < StraightMaxLateral && forwardSpeed > StraightMinSpeed
            && Math.Abs(yawRate) < StraightMaxYawRate && Math.Abs(steerRate) < StraightMaxSteerRate)
        {
            learner.AddStraightSample(steering, dt);
            added = true;
        }

        double lateralLimit = parameters.AyMaxLearned
            ? Math.Min(ModelMaxLateral, ModelMaxLateralFraction * parameters.AyMax)
            : ModelMaxLateral;
        if (steeringKnown && yawKnown)
        {
            CheckSteeringSign(s);
        }

        bool linearQuasiSteady = steeringKnown && yawKnown
            && forwardSpeed > ModelMinSpeed
            && lateralAbs < lateralLimit
            && Math.Abs(steerRate) < ModelMaxSteerRate
            && Math.Abs(yawAccel) < ModelMaxYawAccel
            && Math.Abs(steeringEff) > ModelMinSteerDeg * MathUtil.DegToRad
            && s.Brake < ModelMaxBrake
            && Math.Abs(yawRate) > tuning.RFloor;
        if (linearQuasiSteady && modelInputsOk && IsSteeringOffsetKnown()
            && MathUtil.Sign(steeringEff) == MathUtil.Sign(yawRate)
            && Math.Abs(yawSteady - yawRef) <= ModelSettledTolerance * Math.Abs(yawSteady))
        {
            learner.AddModelSample(forwardSpeed, steeringEff, yawRate, dt);
            added = true;
        }

        // Normal body-slip envelope. Oversteer is judged without the body-slip detector itself, which is what the
        // envelope calibrates (otherwise a car with a naturally large slip angle could never learn it). The raw
        // yaw/countersteer values are used, so suspected slides stay out even while those outputs are suppressed.
        if (forwardSpeed > BodySlipMinSpeed && parameters.AyMaxLearned && MathUtil.IsFinite(bodySlipDeg)
            && Math.Max(Math.Max(osYawRaw, osCountersteerRaw), osDirect) < BodySlipMaxOversteer)
        {
            learner.AddBodySlipSample(lateralNorm, Math.Abs(bodySlipDeg));
            added = true;
        }

        if (s.HasSlipAngles && MathUtil.IsFinite(alphaFront) && MathUtil.IsFinite(alphaRear)
            && forwardSpeed > SlipAngleMinSpeed && parameters.AyMaxLearned
            && lateralAbs > SlipAngleMinLateralFraction * parameters.AyMax)
        {
            learner.AddSlipAngleSample(alphaFront, alphaRear);
            added = true;
        }

        if (modelInputsOk && MathUtil.IsFinite(yawSteady) && MathUtil.IsFinite(steerRate))
        {
            learner.AddYawTrace(time, dt, yawSteady, yawRate, steerRate, forwardSpeed);
            yawTraceActive = true;
            added = true;
        }
        else
        {
            AbortYawTrace();
        }

        return added;
    }

    // =====================================================================================================
    // Runtime calibration of sign conventions
    // =====================================================================================================

    private void VerifyForwardSign(VehicleState s)
    {
        if (calibration.ForwardSignVerified || !(s.Gear > 0 && s.Throttle > ForwardCheckMinThrottle && Math.Abs(s.V) > ForwardCheckMinSpeed))
        {
            return;
        }

        if (MathUtil.IsFinite(s.Vy) && Math.Abs(s.Vy) > ForwardCheckMaxSideSlip * Math.Abs(s.V))
        {
            return;
        }

        switch (forwardVote.Add(MathUtil.Sign(s.V * calibration.ForwardSign)))
        {
            case SignVerdict.Inverted:
                calibration.ForwardSign = -calibration.ForwardSign;
                calibration.ForwardSignVerified = true;
                CalibrationChanged = true;
                ResetFilters();
                log.Warn("Balance: longitudinal velocity of the telemetry source is inverted; corrected (ForwardSign = " + calibration.ForwardSign + ").");
                break;
            case SignVerdict.Confirmed:
                calibration.ForwardSignVerified = true;
                CalibrationChanged = true;
                log.Info("Balance: forward velocity sign verified.");
                break;
        }
    }

    /// <summary>
    /// Steering-sign verification and, once verified, the consistency monitor. Only clean cornering votes: steady
    /// steering and yaw, off the brakes, and no slide (|β| small when the sim reports the lateral velocity), so a
    /// countersteering driver cannot out-vote the true sign. The lateral level does not matter: sign(θ·r) is
    /// positive in every normal corner up to the grip limit.
    /// </summary>
    private void CheckSteeringSign(VehicleState s)
    {
        bool clean = forwardSpeed > SteeringCheckMinSpeed
            && Math.Abs(steeringEff) > SteeringCheckMinSteerDeg * MathUtil.DegToRad
            && Math.Abs(yawRate) > SteeringCheckMinYawRate
            && Math.Abs(steerRate) < SteeringCheckMaxSteerRate
            && Math.Abs(yawAccel) < SteeringCheckMaxYawAccel
            && s.Brake < SteeringCheckMaxBrake
            && !(Math.Abs(bodySlipDeg) >= SteeringCheckMaxBodySlipDeg); // unknown β does not block the vote
        if (!clean)
        {
            return;
        }

        int vote = MathUtil.Sign(steeringEff * yawRate);
        if (!calibration.SteeringSignVerified)
        {
            VerifySteeringSign(vote);
        }
        else if (!(Math.Abs(bodySlipDeg) >= SteeringMonitorMaxBodySlipDeg)
            && steeringMonitor.Add(vote) == SignVerdict.Inverted)
        {
            // A verified sign that most clean corners contradict (e.g. verified in a slide on a sim without lateral
            // velocity): verify again. The model path is suppressed until the new verdict.
            calibration.SteeringSignVerified = false;
            CalibrationChanged = true;
            steeringVote.Reset();
            AbortYawTrace();
            log.Warn("Balance: the verified steering sign disagrees with most clean corners; verifying it again.");
        }
    }

    private void VerifySteeringSign(int vote)
    {
        switch (steeringVote.Add(vote))
        {
            case SignVerdict.Inverted:
                calibration.SteeringSign = -calibration.SteeringSign;
                calibration.SteeringSignVerified = true;
                CalibrationChanged = true;
                filtersStale = true;
                steeringMonitor.Reset();
                AbortYawTrace();

                // θ0 is learned only with a verified sign, i.e. under the sign that has just been found inverted.
                learner.InvertSteeringOffset();
                log.Warn("Balance: steering angle sign is inverted relative to the yaw rate; corrected (SteeringSign = " + calibration.SteeringSign + ").");
                break;
            case SignVerdict.Confirmed:
                calibration.SteeringSignVerified = true;
                CalibrationChanged = true;
                steeringMonitor.Reset();
                log.Info("Balance: steering sign verified against the yaw rate.");
                break;
        }
    }

    private void DecideGravityConvention(double meanAz)
    {
        const double halfG = MathUtil.Gravity / 2.0;
        if (meanAz > halfG)
        {
            calibration.GravityIncluded = true;
            CalibrationChanged = true;
            log.Info("Balance: vertical acceleration includes gravity.");
        }
        else if (meanAz > -halfG)
        {
            calibration.GravityIncluded = false;
            CalibrationChanged = true;
            log.Info("Balance: vertical acceleration excludes gravity.");
        }
        else
        {
            // Mean near -1 g: a down-positive axis. Not representable in SimCalibration, so airborne detection is
            // switched off for this session instead of blanking the outputs permanently.
            gravityConventionUnknown = true;
            log.Warn("Balance: vertical acceleration convention not recognised (mean " + meanAz.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m/s²); airborne detection disabled.");
        }
    }

    // =====================================================================================================
    // Parameters
    // =====================================================================================================

    private bool IsResolveDue(double wallTime)
    {
        BalanceOverrides overrides = profile.Overrides ?? NoOverrides;
        return resolveRequested
            || learner.Version != resolvedLearnerVersion
            || !(wallTime - lastResolveWallTime < ResolveInterval)
            || !SameOverrides(overrides, resolvedOverrides);
    }

    /// <summary>
    /// Resolves the effective parameters. The resolver also hands the non-learned K to the learner
    /// (<c>SetKPrior</c>), so the learner's version recorded afterwards already includes that change.
    /// </summary>
    private void ResolveParameters(double wallTime)
    {
        BalanceOverrides overrides = profile?.Overrides ?? NoOverrides;
        resolver.Resolve(overrides, learner, carClass, profile?.CarKey ?? string.Empty, thetaMax, parameters);
        overrides.CopyTo(resolvedOverrides);
        resolvedLearnerVersion = learner.Version;
        lastResolveWallTime = wallTime;
        resolveRequested = false;
        ApplySanitizedParameters();
    }

    /// <summary>Copies the resolver output into the model fields, falling back to the global defaults for non-finite values.</summary>
    private void ApplySanitizedParameters()
    {
        gainG = MathUtil.IsFinite(parameters.G) && parameters.G > 0.0 ? parameters.G : tuning.GDefault;
        understeerK = MathUtil.IsFinite(parameters.K) && parameters.K >= 0.0 ? parameters.K : tuning.KDefault;
        theta0 = MathUtil.FiniteOr(parameters.Theta0, 0.0);
        tauYaw = MathUtil.IsFinite(parameters.TauYaw) && parameters.TauYaw >= 0.0 ? parameters.TauYaw : tuning.TauYawDefault;
        betaOnsetDeg = MathUtil.IsFinite(parameters.BetaOnsetMarginDeg) ? parameters.BetaOnsetMarginDeg : tuning.BetaOnsetMarginDeg;
        betaFullDeg = MathUtil.IsFinite(parameters.BetaFullMarginDeg) ? parameters.BetaFullMarginDeg : tuning.BetaFullMarginDeg;
    }

    /// <summary>
    /// Spec 5.2: θ0 is fitted first, then fed into the G/K regression. Before it is known, an offset of a degree or
    /// two biases y = v·θeff/r by ±10 % with opposite signs in left and right corners, which tilts the G/K fit.
    /// </summary>
    private bool IsSteeringOffsetKnown() => parameters.Theta0Source != ParamSource.Default;

    private bool IsModelCalibrated() =>
        parameters.GSource == ParamSource.Manual || parameters.GSource == ParamSource.Learned || parameters.GSource == ParamSource.Session;

    private static bool SameOverrides(BalanceOverrides a, BalanceOverrides b) =>
        a.SteeringRatio == b.SteeringRatio && a.WheelbaseM == b.WheelbaseM && a.G == b.G && a.K == b.K
        && a.Theta0Deg == b.Theta0Deg && a.TauYawS == b.TauYawS && a.ClassPreset == b.ClassPreset;

    // =====================================================================================================
    // Timeline, gates and state management
    // =====================================================================================================

    private double AdvanceWallClock(VehicleState s)
    {
        double wall = s != null ? s.WallTime : double.NaN;
        if (!MathUtil.IsFinite(wall))
        {
            return 0.0;
        }

        double step = MathUtil.IsFinite(lastWallTime) ? MathUtil.Clamp(wall - lastWallTime, 0.0, MaxIdleStep) : 0.0;
        lastWallTime = wall;
        return step;
    }

    private static BalanceGate EvaluateHardGates(VehicleState s)
    {
        if (s.IsReplay || s.IsSpectating)
        {
            return BalanceGate.Replay;
        }

        if (!s.OnTrack || s.InGarage)
        {
            return BalanceGate.NotOnTrack;
        }

        return s.IsPaused ? BalanceGate.Paused : BalanceGate.Active;
    }

    private bool IsTeleport(VehicleState s) =>
        Math.Abs((s.V * calibration.ForwardSign) - lastForwardSpeed) > TeleportSpeedJump;

    /// <summary>First valid sample after <see cref="LoadCar"/> or <see cref="Reset"/>: starts the timeline with a blank.</summary>
    private void StartTimeline(VehicleState s, double time)
    {
        initialized = true;
        ResetFilters();
        RestartTimers(time);
        blankUntil = time + tuning.ResetBlankTime;
        lastTime = time;
        lastNewSampleWallTime = s.WallTime;
        lastContactCounter = s.ContactCounter;
    }

    private void HandleDiscontinuity(VehicleState s, double time, bool blank)
    {
        ResetFilters();
        AbortYawTrace();
        if (blank)
        {
            RestartTimers(time);
            blankUntil = time + tuning.ResetBlankTime;
            lastContactCounter = s.ContactCounter;
        }

        lastTime = time;
        lastNewSampleWallTime = s.WallTime;
    }

    /// <summary>Re-bases every timestamp on <paramref name="time"/> (needed when the sim clock restarts).</summary>
    private void RestartTimers(double time)
    {
        onTrackSince = time;
        blankUntil = double.NegativeInfinity;
        contactUntil = double.NegativeInfinity;
        airborneUntil = double.NegativeInfinity;
        lastContactTime = double.NegativeInfinity;
        lastAirborneTime = double.NegativeInfinity;
        spinSince = double.NaN;
    }

    private void ResetFilters()
    {
        yawMedian.Reset();
        yawEma.Reset();
        steeringEma.Reset();
        lateralVelocityEma.Reset();
        forwardSpeedEma.Reset();
        alphaFrontEma.Reset();
        alphaRearEma.Reset();
        verticalMedian.Reset();
        yawRef = double.NaN;
        yawRate = double.NaN;
        steering = double.NaN;
        lastForwardSpeed = double.NaN;
    }

    /// <summary>Everything except the loaded car and its learned baseline (LoadCar, Unload, Reset).</summary>
    private void ResetRuntimeState()
    {
        initialized = false;
        filtersStale = false;
        wasOffTrack = false;
        ResetFilters();
        RestartTimers(0.0);
        understeerHysteresis.Reset();
        oversteerHysteresis.Reset();
        understeerEnvelope.Reset();
        oversteerEnvelope.Reset();
        forwardVote.Reset();
        steeringVote.Reset();
        steeringMonitor.Reset();
        verticalMean.Reset();
        gravitySamples = 0;
        gravityConventionUnknown = false;
        AbortYawTrace();
        resolveRequested = true;
        lastResolveWallTime = double.NegativeInfinity;
        thetaMax = double.NaN;
        spinning = false;
    }

    private void AbortYawTrace()
    {
        if (yawTraceActive)
        {
            yawTraceActive = false;
            learner.AbortYawEvent();
        }
    }

    // =====================================================================================================
    // Outputs
    // =====================================================================================================

    /// <summary>No sample is processed this tick: outputs decay to 0 over <paramref name="wallStep"/> and debug signals clear.</summary>
    private void Idle(BalanceGate gate, double wallStep)
    {
        understeerHysteresis.Reset();
        oversteerHysteresis.Reset();
        double us = understeerEnvelope.Update(0.0, wallStep, tuning.Attack, tuning.Release);
        double os = oversteerEnvelope.Update(0.0, wallStep, tuning.Attack, tuning.Release);
        AbortYawTrace();
        spinSince = double.NaN;
        spinning = false;
        lastForwardSpeed = double.NaN;

        Outputs.Understeer = us;
        Outputs.Oversteer = os;
        Outputs.PowerOversteer = false;
        Outputs.LiftOrBrakeOversteer = false;
        Outputs.EntryUndersteer = false;
        Outputs.ExitUndersteer = false;
        Outputs.Countersteer = false;
        Outputs.Spin = false;
        Outputs.Active = false;
        Outputs.Confidence = 0.0;
        Outputs.Gate = gate;
        Outputs.Path = BalancePath.None;
        Outputs.LearningActive = false;
        ClearSignals();
        PublishSignals();
    }

    private void ClearSignals()
    {
        yawRate = double.NaN;
        steering = double.NaN;
        steeringEff = double.NaN;
        yawSteady = double.NaN;
        yawRatio = double.NaN;
        bodySlipDeg = double.NaN;
        alphaFront = double.NaN;
        alphaRear = double.NaN;
        speedRamp = 0.0;
        usModel = 0.0;
        osYaw = 0.0;
        osCountersteer = 0.0;
        osBodySlip = 0.0;
        osModel = 0.0;
        usDirect = 0.0;
        osDirect = 0.0;
        osYawRaw = 0.0;
        osCountersteerRaw = 0.0;
    }

    /// <summary>Resets <see cref="Outputs"/> (car/game change, reset) and republishes the loaded car's parameters.</summary>
    private void ClearOutputs()
    {
        Outputs.Clear();
        ClearSignals();
        if (profile != null)
        {
            PublishParameters();
        }
    }

    private void PublishSignals()
    {
        double alphaScale = slipAnglesInRadians ? MathUtil.RadToDeg : 1.0;
        Outputs.YawRate = yawRate;
        Outputs.YawRef = MathUtil.IsFinite(steering) ? yawRef : double.NaN;
        Outputs.YawRatio = yawRatio;
        Outputs.BodySlipDeg = bodySlipDeg;
        Outputs.SteeringDeg = steering * MathUtil.RadToDeg;
        Outputs.SteeringEffDeg = steeringEff * MathUtil.RadToDeg;
        Outputs.SpeedRamp = speedRamp;
        Outputs.UsModel = usModel;
        Outputs.OsModel = osModel;
        Outputs.UsDirect = usDirect;
        Outputs.OsDirect = osDirect;
        Outputs.OsYaw = osYaw;
        Outputs.OsCountersteer = osCountersteer;
        Outputs.OsBodySlip = osBodySlip;
        Outputs.AlphaFront = alphaFront * alphaScale;
        Outputs.AlphaRear = alphaRear * alphaScale;
    }

    private void PublishParameters()
    {
        Outputs.G = gainG;
        Outputs.K = understeerK;
        Outputs.Theta0Deg = theta0 * MathUtil.RadToDeg;
        Outputs.TauYaw = tauYaw;
        Outputs.AyMax = parameters.AyMax;
        Outputs.AlphaPeak = parameters.AlphaPeak * (slipAnglesInRadians ? MathUtil.RadToDeg : 1.0);
        Outputs.GSource = parameters.GSource;
        Outputs.KSource = parameters.KSource;
        Outputs.Theta0Source = parameters.Theta0Source;
        Outputs.TauSource = parameters.TauSource;
        Outputs.ConfG = parameters.ConfG;
        Outputs.ConfK = parameters.ConfK;
        Outputs.ClassPreset = parameters.ClassPreset;
        Outputs.SamplesG = learner.G.Samples;
        Outputs.SamplesK = learner.K.Samples;
        Outputs.SamplesTheta0 = learner.Theta0.Samples;
        Outputs.SamplesTau = learner.TauYaw.Samples;
        Outputs.SamplesAy = learner.AyMax.Samples;
        Outputs.SamplesAlpha = learner.AlphaPeak.Samples;
        Outputs.LearningLocked = learner.Locked;
        Outputs.SessionOverrideActive = learner.SessionGActive || learner.SessionTheta0Active;
        Outputs.SteeringSign = calibration.SteeringSign;
        Outputs.ForwardSign = calibration.ForwardSign;
        Outputs.SteeringSignVerified = calibration.SteeringSignVerified;
        Outputs.GravityIncluded = calibration.GravityIncluded;
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    private double SpeedRamp(double speed)
    {
        double span = tuning.VFull - tuning.VMin;
        if (!(span > 0.0))
        {
            return speed >= tuning.VMin ? 1.0 : 0.0;
        }

        return MathUtil.Clamp01((speed - tuning.VMin) / span);
    }

    /// <summary>Median then EMA; a non-finite sample resets both so a later sample restarts cleanly.</summary>
    private static double MedianThenEma(Median3 median, Ema ema, double x, double alpha)
    {
        if (!MathUtil.IsFinite(x))
        {
            median.Reset();
            ema.Reset();
            return double.NaN;
        }

        return ema.Update(median.Update(x), alpha);
    }

    private static double EmaOrReset(Ema ema, double x, double alpha)
    {
        if (!MathUtil.IsFinite(x))
        {
            ema.Reset();
            return double.NaN;
        }

        return ema.Update(x, alpha);
    }
}
