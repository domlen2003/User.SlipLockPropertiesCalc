using System;
using System.Globalization;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Balance;

/// <summary>
/// Learns the per-car vehicle model the understeer/oversteer estimator needs (spec 5.2/5.3): steering gain G and
/// understeer factor K (robust recursive least squares), steering offset θ0, steering→yaw lag τ, the lateral
/// acceleration limit ay_max, the normal body-slip envelope β_env(|ay|/ay_max) and the near-peak slip angle α_peak.
/// </summary>
/// <remarks>
/// <para>
/// Two layers: the <b>baseline</b> is what gets persisted per car (<see cref="SaveTo"/>/<see cref="Load"/>); the
/// <b>session</b> layer is a fast copy for G and θ0 that follows damage, a bent steering rack or changing conditions
/// within one session. It is compared with a reference taken from the baseline, not with the live baseline, because
/// the still-learning baseline would drift after a sudden change and hide it. The session layer never writes into
/// the baseline; while it overrides a parameter the baseline stops learning that parameter, so a damaged car does
/// not leak into the persisted profile. <see cref="Locked"/> freezes the whole baseline while the session layer
/// keeps running.
/// </para>
/// <para>
/// References: θ0 is raw straight-line steering, so its reference is the trusted baseline at session start, or as
/// soon as the baseline becomes trusted. G samples depend on inputs that are learned too (θ0 enters θeff, K decides
/// the fitted G): a G reference is only taken at session start from a baseline that was already trusted (spec 5.3:
/// "on top of the persisted baseline"), and it is dropped for the rest of the session when those inputs change.
/// Otherwise the session would report the learning of θ0 or K as a change of G.
/// </para>
/// <para>
/// The caller (the estimator) applies the learning gates of spec 5.2; this class only rejects implausible or
/// non-finite samples and outliers. Every Add method never throws and is allocation-free, apart from one log line
/// when a session override switches on or off (a rare state change). Not thread-safe: use it from the data thread
/// only.
/// </para>
/// <para>
/// Published results are <see cref="LearnedParam"/>s whose <c>Value</c> is NaN (and confidence 0) until enough
/// samples exist. <see cref="Version"/> increments on every change of learner state, so consumers can cache
/// anything derived from the results.
/// </para>
/// </remarks>
internal sealed class BalanceLearner
{
    // ---- Steering gain G and understeer factor K ----

    /// <summary>G is published once the outlier gate of the fit is open.</summary>
    private const long MinSamplesG = SteeringGainFit.WarmupSamples;

    private const double FullConfidenceSamplesG = 300;

    /// <summary>K needs many samples and a wide speed spread: it is the speed dependence of the gain.</summary>
    private const long MinSamplesK = 800;

    private const double FullConfidenceSamplesK = 800;
    private const double MinSpeedSpreadK = 15.0; // m/s

    /// <summary>
    /// Fit quality q = 1 / (1 + (rms / ref)²) of the relative model error: R²-like with a fixed reference error
    /// instead of the y variance, because a car with K = 0 has constant y and R² would call a perfect fit useless.
    /// With a 15 % reference, typical 3–5 % yaw-rate noise gives q ≈ 0.9–0.96.
    /// </summary>
    private const double FitQualityReferenceError = 0.15;

    // Plausible ranges (spec 5.2). Values outside by more than the tolerance are clamped and halve the confidence;
    // within the tolerance they are treated as noise (e.g. K = -0.0001 for a neutral car) and clamped silently.
    private const double GMin = 0.005;
    private const double GMax = 0.1;
    private const double GClampRelativeTolerance = 0.05;
    private const double KMin = 0.0;
    private const double KMax = 0.004;
    private const double KClampTolerance = 0.0002;
    private const double ClampConfidencePenalty = 0.5;

    /// <summary>Upper bound for the K prior handed in by the resolver (same range as BalanceTuning.KDefault).</summary>
    private const double MaxKPrior = 0.05;

    // Model samples outside these are meaningless (y = v·θ/r explodes, or a telemetry glitch would get huge leverage
    // through x = v²); the estimator's gates are far stricter.
    private const double MinModelSpeed = 1.0;      // m/s
    private const double MaxModelSpeed = 150.0;    // m/s (540 km/h)
    private const double MinModelYawRate = 1e-3;   // rad/s

    // ---- Steering offset θ0 ----
    private const double Theta0TimeConstant = 10.0;        // s (spec: EMA of theta on straights)
    private const double SessionTheta0TimeConstant = 3.0;  // s
    private const long MinSamplesTheta0 = 30;
    private const double FullConfidenceSamplesTheta0 = 300;
    private const double Theta0LimitRad = 10.0 * MathUtil.DegToRad;
    private const double Theta0ClampToleranceRad = 0.5 * MathUtil.DegToRad;

    /// <summary>Straight-line steering beyond this is a glitch (units, sign), not an offset; ignored.</summary>
    private const double MaxStraightSteeringRad = 40.0 * MathUtil.DegToRad;

    /// <summary>Longest dt (s) one EMA step may weigh, so a hitch in the sample rate cannot dominate the average.</summary>
    private const double MaxEmaStep = 0.1;

    // ---- ay_max: 98th percentile of |ay| ----
    private const double AyRangeMax = 50.0;  // m/s² (above: counted in the top bin)
    private const int AyBinCount = 200;      // 0.25 m/s² bins

    /// <summary>|ay| beyond 10 g is an impact or a glitch, never cornering; ignored.</summary>
    private const double MaxPlausibleAy = 100.0;
    private const double AyPercentile = 0.98;
    private const long MinSamplesAyMax = 1500;
    private const double FullConfidenceSamplesAyMax = 3000;

    // ---- α_peak: 90th percentile of max(α_front, α_rear) near the grip limit, unit-agnostic log bins ----
    private const double AlphaRangeMin = 1e-4;
    private const double AlphaRangeMax = 100.0;
    private const int AlphaBinCount = 120;
    private const double AlphaPercentile = 0.90;
    private const long MinSamplesAlphaPeak = 300;
    private const double FullConfidenceSamplesAlphaPeak = 600;

    /// <summary>Histograms are halved above this many samples (about an hour of qualifying driving at 60 Hz).</summary>
    private const long HistogramForgetThreshold = 200000;

    // ---- Session adaptation layer (spec 5.3) ----
    private const long SessionMinSamples = 150;
    private const double SessionGDeviation = 0.15;

    /// <summary>Consecutive deviating samples before the session G takes over (decays by 2 per agreeing sample).</summary>
    private const int SessionGActivationCount = 60;

    private const int SessionGDecayStep = 2;
    private const double SessionTheta0OnRad = 1.5 * MathUtil.DegToRad;
    private const double SessionTheta0OffRad = 1.0 * MathUtil.DegToRad;

    /// <summary>
    /// A change of the θ0 used for θeff larger than this since the G reference was taken invalidates the reference:
    /// it shifts y = v·θeff/r (and so the fitted G) by several percent at typical steering angles.
    /// </summary>
    private const double ReferenceTheta0ToleranceRad = 0.5 * MathUtil.DegToRad;

    /// <summary>The session fit starts from the baseline worth this many samples, then forgets it within seconds.</summary>
    private const double SessionSeedSamples = 50;

    private readonly BalanceTuning tuning;
    private readonly ILog log;

    private readonly SteeringGainFit baselineFit = new SteeringGainFit();
    private readonly SteeringGainFit sessionFit = new SteeringGainFit();
    private readonly WarmStartEma baselineTheta0 = new WarmStartEma();
    private readonly WarmStartEma sessionTheta0 = new WarmStartEma();
    private readonly Histogram ayHistogram = Histogram.Linear(0.0, AyRangeMax, AyBinCount, HistogramForgetThreshold);
    private readonly BetaEnvelope betaEnvelope = new BetaEnvelope(HistogramForgetThreshold);
    private readonly Histogram alphaHistogram = Histogram.Logarithmic(AlphaRangeMin, AlphaRangeMax, AlphaBinCount, HistogramForgetThreshold);
    private readonly YawLagEstimator yawLag = new YawLagEstimator();

    private long aySamples;
    private long betaSamples;
    private long alphaSamples;

    private double kPrior;
    private bool kPriorIsManual;

    /// <summary>The K the published G (and the session G) is fitted with.</summary>
    private double kForG;

    /// <summary>True when <see cref="kForG"/> is the learned K (it then drifts slightly with every sample).</summary>
    private bool kForGIsLearned;

    private double sessionGRaw = double.NaN;
    private int sessionGCounter;

    /// <summary>Model samples the session layer accepted itself since the last restart (the seed not counted).</summary>
    private long sessionOwnModelSamples;

    // Model inputs SessionReferenceG was fitted with: the K source (a learned K may drift, a change of source may not)
    // and the θ0 used for θeff.
    private bool sessionReferenceUsesLearnedK;
    private double sessionReferenceKPrior = double.NaN;
    private double sessionReferenceTheta0InUse;

    private bool locked;
    private int version;

    /// <param name="tuning">Live global tuning (read on every sample: forgetting factors, defaults, threshold).</param>
    /// <param name="log">Receives rare state changes (session overrides, invalid persisted data). May be null.</param>
    public BalanceLearner(BalanceTuning tuning, ILog log)
    {
        this.tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        this.log = log ?? NullLog.Instance;
        kPrior = MathUtil.IsFinite(tuning.KDefault) ? MathUtil.Clamp(tuning.KDefault, 0.0, MaxKPrior) : 0.0;
        kForG = kPrior;
        ClearBaseline();
        RestartSession();
    }

    /// <summary>Freezes the persisted baseline (spec "lock learning"); the session layer keeps running.</summary>
    public bool Locked
    {
        get => locked;
        set
        {
            if (value)
            {
                yawLag.AbortEvent();
            }

            locked = value;
        }
    }

    /// <summary>Baseline changed since the last <see cref="SaveTo"/> or <see cref="Load"/>.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Increments whenever any learner state (and therefore possibly a result or confidence) changes.</summary>
    public int Version => version;

    /// <summary>Baseline steering gain G (1/m).</summary>
    public LearnedParam G { get; } = new LearnedParam();

    /// <summary>Baseline understeer factor K (s²/m²); NaN until ≥ 800 samples with ≥ 15 m/s speed spread.</summary>
    public LearnedParam K { get; } = new LearnedParam();

    /// <summary>Baseline steering offset θ0 (steering-wheel radians).</summary>
    public LearnedParam Theta0 { get; } = new LearnedParam();

    /// <summary>Steering→yaw lag τ (s); samples = accepted turn-in events.</summary>
    public LearnedParam TauYaw { get; } = new LearnedParam();

    /// <summary>98th percentile of |ay| (m/s²).</summary>
    public LearnedParam AyMax { get; } = new LearnedParam();

    /// <summary>Near-peak slip angle (sim-native unit).</summary>
    public LearnedParam AlphaPeak { get; } = new LearnedParam();

    /// <summary>Speed spread (m/s) of the recent model samples (≈ max − min, from their forgetting-weighted spread).</summary>
    public double VSpread => baselineFit.SpeedSpread;

    /// <summary>True while the session layer's G replaces the baseline G.</summary>
    public bool SessionGActive { get; private set; }

    /// <summary>Session-layer G (1/m), NaN before the first session sample. Fitted with the same K as <see cref="G"/>.</summary>
    public double SessionG { get; private set; } = double.NaN;

    /// <summary>True while the session layer's θ0 replaces the baseline θ0.</summary>
    public bool SessionTheta0Active { get; private set; }

    /// <summary>Session-layer θ0 (steering-wheel radians), NaN before the first session straight sample.</summary>
    public double SessionTheta0 { get; private set; } = double.NaN;

    // ---- Diagnostics (debug view) ----

    /// <summary>
    /// Baseline G the session G is compared with: taken at session start if the baseline was trusted then; NaN when
    /// there is none or the model inputs changed during the session (then no session G override this session).
    /// </summary>
    public double SessionReferenceG { get; private set; } = double.NaN;

    /// <summary>Trusted baseline θ0 (rad) the session θ0 is compared with (NaN while the baseline is not trusted).</summary>
    public double SessionReferenceTheta0 { get; private set; } = double.NaN;

    /// <summary>The K currently used as prior (manual, class preset or default).</summary>
    public double KPrior => kPrior;

    /// <summary>True when the K prior is a manual override (G is then always fitted with it).</summary>
    public bool KPriorIsManual => kPriorIsManual;

    /// <summary>Model samples the baseline rejected as outliers or implausible (lifetime).</summary>
    public long RejectedModelSamples => baselineFit.Rejected;

    /// <summary>Model samples the session layer accepted since the last session reset.</summary>
    public long SessionModelSamples => sessionFit.Samples;

    /// <summary>Straight-line samples the session layer received since the last session reset.</summary>
    public long SessionStraightSamples => sessionTheta0.Count;

    /// <summary>True while a turn-in event is being recorded for τ.</summary>
    public bool YawEventInProgress => yawLag.EventInProgress;

    /// <summary>Number of |ay|/ay_max bins of the β envelope that are learned (0..10).</summary>
    public int BetaEnvelopeLearnedBins => betaEnvelope.LearnedBinCount;

    /// <summary>
    /// Replaces the baseline with <paramref name="state"/> (null or partial data → empty accumulators; a newer schema
    /// is ignored) and restarts the session layer from it. Clears <see cref="Dirty"/>. Allocates; call on car load.
    /// </summary>
    public void Load(BalanceLearnedState state)
    {
        ClearBaseline();
        if (state != null)
        {
            if (state.SchemaVersion > BalanceLearnedState.CurrentSchemaVersion)
            {
                log.Warn(string.Format(
                    CultureInfo.InvariantCulture,
                    "Balance learner: learned data has schema version {0}, newer than {1}; starting with an empty model.",
                    state.SchemaVersion,
                    BalanceLearnedState.CurrentSchemaVersion));
            }
            else if (!LoadBaseline(state))
            {
                log.Warn("Balance learner: part of the learned data was invalid and has been reset.");
            }
        }

        RestartSession();
        Dirty = false;
        version++;
    }

    /// <summary>Writes the complete baseline (and a readable summary) into <paramref name="state"/>; clears <see cref="Dirty"/>. Allocates.</summary>
    public void SaveTo(BalanceLearnedState state)
    {
        if (state == null)
        {
            return;
        }

        state.SchemaVersion = BalanceLearnedState.CurrentSchemaVersion;

        state.G = FiniteOrNull(G.Value);
        state.GConfidence = G.Confidence;
        state.K = FiniteOrNull(K.Value);
        state.KConfidence = K.Confidence;
        state.Theta0Deg = FiniteOrNull(Theta0.Value * MathUtil.RadToDeg);
        state.Theta0Confidence = Theta0.Confidence;
        state.TauYawS = FiniteOrNull(TauYaw.Value);
        state.TauYawConfidence = TauYaw.Confidence;
        state.AyMax = FiniteOrNull(AyMax.Value);
        state.AyMaxConfidence = AyMax.Confidence;
        state.AlphaPeak = FiniteOrNull(AlphaPeak.Value);
        state.AlphaPeakConfidence = AlphaPeak.Confidence;

        baselineFit.SaveTo(state);
        state.Theta0Ema = baselineTheta0.Value;
        state.Theta0Samples = baselineTheta0.Count;
        state.AyHistogram = ayHistogram.ToTrimmedArray();
        state.AySamples = aySamples;
        state.BetaHistograms = betaEnvelope.ToTrimmedArrays();
        state.BetaSamples = betaSamples;
        state.AlphaHistogram = alphaHistogram.ToTrimmedArray();
        state.AlphaSamples = alphaSamples;
        state.TauEvents = yawLag.ToArray();
        state.TauEventsTotal = yawLag.EventsTotal;

        Dirty = false;
    }

    /// <summary>Forgets everything learned for this car, including the session layer (<see cref="Dirty"/> becomes true).</summary>
    public void ResetBaseline()
    {
        ClearBaseline();
        RestartSession();
        Dirty = true;
        version++;
    }

    /// <summary>Restarts the session adaptation layer from the current baseline (new session, car reload).</summary>
    public void ResetSession()
    {
        RestartSession();
        version++;
    }

    /// <summary>K used by the 1-parameter G estimate (the resolver's non-learned K: manual, else preset, else default).</summary>
    public void SetKPrior(double kPrior) => SetKPrior(kPrior, isManual: false);

    /// <summary>
    /// Sets the K prior. With <paramref name="isManual"/> the K is a user override: G is then always fitted with it,
    /// even when the learner has its own trusted K, so the G/K pair the estimator uses stays consistent.
    /// Non-finite values are ignored; the value is clamped to [0, 0.05].
    /// </summary>
    public void SetKPrior(double kPrior, bool isManual)
    {
        if (!MathUtil.IsFinite(kPrior))
        {
            return;
        }

        double k = MathUtil.Clamp(kPrior, 0.0, MaxKPrior);
        if (k == this.kPrior && isManual == kPriorIsManual)
        {
            return;
        }

        this.kPrior = k;
        kPriorIsManual = isManual;
        RefreshBaselinePrior();
        PublishGainResults();
        if (sessionOwnModelSamples == 0)
        {
            // Nothing was compared with the session references yet, so this is not a change of the model inputs
            // during the session but the car's prior arriving after Load (the estimator resolves K only after
            // loading the learned state): take the references with the prior G is fitted with from now on.
            // Otherwise the first session sample would see a K change and disable the session G override.
            TakeSessionGainReferences();
        }

        version++;
    }

    /// <summary>
    /// The estimator found the steering sign it used to be inverted. θ0 learned so far was measured in the wrong
    /// steering direction, so the baseline offset is negated (also while locked: this corrects the representation,
    /// it is not new learning), and the session layer restarts from the corrected baseline. The G/K fit needs no
    /// correction: while the sign was inverted it rejected normal cornering (steering and yaw disagreed).
    /// </summary>
    public void InvertSteeringOffset()
    {
        if (baselineTheta0.Count > 0)
        {
            baselineTheta0.Restore(-baselineTheta0.Value, baselineTheta0.Count);
            Dirty = true;
        }

        yawLag.AbortEvent();
        RestartSession();
        version++;
    }

    /// <summary>|ay| (m/s²) of a gated sample at speed; feeds ay_max.</summary>
    public void AddLateralSample(double absAy)
    {
        double magnitude = Math.Abs(absAy);
        if (locked || !(magnitude <= MaxPlausibleAy) || !ayHistogram.Add(magnitude))
        {
            return;
        }

        aySamples++;
        Dirty = true;
        PublishAyMax();
        version++;
    }

    /// <summary>Sign-corrected steering-wheel angle (rad, θ0 not subtracted) while driving straight; feeds θ0.</summary>
    public void AddStraightSample(double theta, double dt)
    {
        if (!MathUtil.IsFinite(theta) || !MathUtil.IsFinite(dt) || !(dt > 0) || Math.Abs(theta) > MaxStraightSteeringRad)
        {
            return;
        }

        double step = Math.Min(dt, MaxEmaStep);
        if (!locked && !SessionTheta0Active)
        {
            baselineTheta0.Add(theta, step, Theta0TimeConstant);
            Dirty = true;
            PublishTheta0();
        }

        sessionTheta0.Add(theta, step, SessionTheta0TimeConstant);
        RefreshSessionTheta0();
        UpdateSessionTheta0Override();
        version++;
    }

    /// <summary>
    /// Quasi-steady linear-region sample: speed v (m/s), effective steering θ − θ0 (rad) and yaw rate r (rad/s) with
    /// the same sign. Feeds G/K of the baseline and the session layer.
    /// </summary>
    public void AddModelSample(double v, double thetaEff, double r, double dt)
    {
        if (!MathUtil.IsFinite(v) || !MathUtil.IsFinite(thetaEff) || !MathUtil.IsFinite(r) || !MathUtil.IsFinite(dt)
            || !(dt > 0) || v < MinModelSpeed || v > MaxModelSpeed || Math.Abs(r) < MinModelYawRate)
        {
            return;
        }

        // y <= 0: steering and rotation disagree (countersteer, sign glitch), never a linear-region sample.
        double y = v * thetaEff / r;
        if (!(y > 0))
        {
            return;
        }

        bool changed = false;
        if (!locked && !SessionGActive)
        {
            changed = baselineFit.Add(v, y, tuning.RlsLambda) != FitSampleResult.Ignored;
            Dirty |= changed;
        }

        FitSampleResult sessionResult = sessionFit.Add(v, y, tuning.SessionRlsLambda);
        if (!changed && sessionResult == FitSampleResult.Ignored)
        {
            return;
        }

        PublishGainResults();
        if (sessionResult == FitSampleResult.Accepted)
        {
            sessionOwnModelSamples++;
            UpdateSessionGOverride();
        }

        version++;
    }

    /// <summary>Normalized lateral acceleration |ay|/ay_max and |β| (deg) while not oversteering; feeds β_env.</summary>
    public void AddBodySlipSample(double ayNorm, double absBetaDeg)
    {
        if (locked || !betaEnvelope.Add(ayNorm, absBetaDeg))
        {
            return;
        }

        betaSamples++;
        Dirty = true;
        version++;
    }

    /// <summary>Mean |front| and |rear| slip angle (native unit) near the grip limit; feeds α_peak.</summary>
    public void AddSlipAngleSample(double alphaFront, double alphaRear)
    {
        if (locked || !MathUtil.IsFinite(alphaFront) || !MathUtil.IsFinite(alphaRear)
            || !alphaHistogram.Add(Math.Max(Math.Abs(alphaFront), Math.Abs(alphaRear))))
        {
            return;
        }

        alphaSamples++;
        Dirty = true;
        PublishAlphaPeak();
        version++;
    }

    /// <summary>
    /// One gated tick for the τ estimate: time t (s), requested steady-state yaw rate r_ss and measured r (rad/s),
    /// steering rate (rad/s) and speed (m/s). Turn-in events are detected internally.
    /// </summary>
    public void AddYawTrace(double t, double dt, double rSs, double r, double dThetaDt, double v)
    {
        if (locked)
        {
            yawLag.AbortEvent();
            return;
        }

        if (!yawLag.AddTrace(t, dt, rSs, r, dThetaDt, v))
        {
            return;
        }

        Dirty = true;
        PublishTau();
        version++;
    }

    /// <summary>A learning gate closed: drop the turn-in event being recorded.</summary>
    public void AbortYawEvent() => yawLag.AbortEvent();

    /// <summary>Normal |β| (deg) of this car at |ay|/ay_max = <paramref name="ayNorm"/>; 0 until learned.</summary>
    public double BetaEnvelopeDeg(double ayNorm) => betaEnvelope.Evaluate(ayNorm);

    private static double? FiniteOrNull(double value) => MathUtil.IsFinite(value) ? value : (double?)null;

    private static double FitQuality(double rmsRelativeError)
    {
        if (!MathUtil.IsFinite(rmsRelativeError))
        {
            return 0.0;
        }

        double ratio = rmsRelativeError / FitQualityReferenceError;
        return 1.0 / (1.0 + (ratio * ratio));
    }

    /// <summary>
    /// Clamps to [min, max]. Outside [min − lowerTolerance, max + upperTolerance] the raw value is implausible,
    /// so the confidence is halved (spec: "if clamping was needed halve the confidence").
    /// </summary>
    private static double ClampWithPenalty(double value, double min, double max, double lowerTolerance, double upperTolerance, ref double confidence)
    {
        if (value < min - lowerTolerance || value > max + upperTolerance)
        {
            confidence *= ClampConfidencePenalty;
        }

        return MathUtil.Clamp(value, min, max);
    }

    private static void Publish(LearnedParam target, double value, double confidence, long samples)
    {
        target.Value = value;
        target.Confidence = MathUtil.IsFinite(value) ? MathUtil.Clamp01(confidence) : 0.0;
        target.Samples = samples;
    }

    private bool LoadBaseline(BalanceLearnedState state)
    {
        bool valid = baselineFit.Load(state);

        if (MathUtil.IsFinite(state.Theta0Ema) && state.Theta0Samples >= 0 && Math.Abs(state.Theta0Ema) <= MaxStraightSteeringRad)
        {
            baselineTheta0.Restore(state.Theta0Ema, state.Theta0Samples);
        }
        else
        {
            valid = false;
        }

        valid &= ayHistogram.Load(state.AyHistogram);
        aySamples = Math.Max(state.AySamples, ayHistogram.Total);
        valid &= betaEnvelope.Load(state.BetaHistograms);
        betaSamples = Math.Max(state.BetaSamples, 0);
        valid &= alphaHistogram.Load(state.AlphaHistogram);
        alphaSamples = Math.Max(state.AlphaSamples, alphaHistogram.Total);
        valid &= yawLag.Load(state.TauEvents, state.TauEventsTotal);
        return valid;
    }

    private void ClearBaseline()
    {
        baselineFit.Clear();
        RefreshBaselinePrior();
        baselineTheta0.Clear();
        ayHistogram.Clear();
        aySamples = 0;
        betaEnvelope.Clear();
        betaSamples = 0;
        alphaHistogram.Clear();
        alphaSamples = 0;
        yawLag.Clear();
    }

    /// <summary>The weak prior of the 2-parameter fit: default G and the best non-learned K.</summary>
    private void RefreshBaselinePrior()
    {
        double a0 = 1.0 / MathUtil.Clamp(MathUtil.FiniteOr(tuning.GDefault, GMin), GMin, GMax);
        baselineFit.SetPriorMean(a0, kPrior * a0 * SteeringGainFit.SpeedSquaredScale);
    }

    /// <summary>
    /// Publishes the baseline, takes the session references from it and restarts the session layer as a
    /// down-weighted copy of the baseline fit.
    /// </summary>
    private void RestartSession()
    {
        sessionFit.SeedFrom(baselineFit, SessionSeedSamples);
        sessionTheta0.Clear();
        sessionGCounter = 0;
        sessionOwnModelSamples = 0;
        SessionGActive = false;
        SessionTheta0Active = false;

        PublishTheta0();
        PublishAyMax();
        PublishAlphaPeak();
        PublishTau();
        PublishGainResults(); // also refreshes the (now empty) session G

        TakeSessionGainReferences();
        SessionReferenceTheta0 = IsTrusted(Theta0) ? Theta0.Value : double.NaN;
        RefreshSessionTheta0();
    }

    /// <summary>The baseline G and the model inputs it is fitted with, which the session G is compared against.</summary>
    private void TakeSessionGainReferences()
    {
        SessionReferenceG = IsTrusted(G) ? G.Value : double.NaN;
        sessionReferenceUsesLearnedK = kForGIsLearned;
        sessionReferenceKPrior = kPrior;
        sessionReferenceTheta0InUse = Theta0InUse();
    }

    /// <summary>The θ0 the estimator most likely subtracts from the steering: session override, else trusted baseline, else 0.</summary>
    private double Theta0InUse()
    {
        if (SessionTheta0Active && MathUtil.IsFinite(SessionTheta0))
        {
            return SessionTheta0;
        }

        return IsTrusted(Theta0) ? Theta0.Value : 0.0;
    }

    /// <summary>True when G is now fitted with other inputs (K source, θ0) than when the session reference was taken.</summary>
    private bool ModelInputsChangedSinceReference() =>
        kForGIsLearned != sessionReferenceUsesLearnedK
        || (!kForGIsLearned && kPrior != sessionReferenceKPrior)
        || Math.Abs(Theta0InUse() - sessionReferenceTheta0InUse) > ReferenceTheta0ToleranceRad;

    private bool IsTrusted(LearnedParam param) =>
        MathUtil.IsFinite(param.Value) && param.Confidence >= tuning.LearnedConfidenceThreshold;

    private void PublishGainResults()
    {
        long n = baselineFit.Samples;

        // K first: whether it is trusted decides which K the gain G is fitted with.
        bool kLearned = false;
        if (n >= MinSamplesK && baselineFit.SpeedSpread >= MinSpeedSpreadK
            && baselineFit.TryGetTwoParameterFit(out double kRaw, out double rmsTwoParameter))
        {
            double confidence = Math.Min(1.0, n / FullConfidenceSamplesK) * FitQuality(rmsTwoParameter);
            double k = ClampWithPenalty(kRaw, KMin, KMax, KClampTolerance, KClampTolerance, ref confidence);
            Publish(K, k, confidence, n);
            kLearned = true;
        }
        else
        {
            Publish(K, double.NaN, 0.0, n);
        }

        kForGIsLearned = kLearned && !kPriorIsManual && K.Confidence >= tuning.LearnedConfidenceThreshold;
        kForG = kForGIsLearned ? K.Value : kPrior;

        if (n >= MinSamplesG && baselineFit.TryGetGain(kForG, out double gRaw, out double rmsOneParameter))
        {
            double confidence = Math.Min(1.0, n / FullConfidenceSamplesG) * FitQuality(rmsOneParameter);
            double g = ClampWithPenalty(
                gRaw,
                GMin,
                GMax,
                GMin * GClampRelativeTolerance,
                GMax * GClampRelativeTolerance,
                ref confidence);
            Publish(G, g, confidence, n);
        }
        else
        {
            Publish(G, double.NaN, 0.0, n);
        }

        // Same K as the baseline G, so the session G stays comparable with the reference.
        if (sessionFit.Samples > 0 && sessionFit.TryGetGain(kForG, out double gSession, out _))
        {
            sessionGRaw = gSession;
            SessionG = MathUtil.Clamp(gSession, GMin, GMax);
        }
        else
        {
            sessionGRaw = double.NaN;
            SessionG = double.NaN;
        }
    }

    /// <summary>
    /// Spec 5.3: the session G takes over after a persistent deviation (≥ 60 deviating accepted samples, the count
    /// decays by 2 per agreeing sample) of more than 15 % from the reference; it is released when the count is 0.
    /// </summary>
    private void UpdateSessionGOverride()
    {
        if (MathUtil.IsFinite(SessionReferenceG) && ModelInputsChangedSinceReference())
        {
            // The session and the reference would now compare G fitted with different inputs: not meaningful.
            SessionReferenceG = double.NaN;
        }

        bool eligible = sessionFit.Samples >= SessionMinSamples
            && MathUtil.IsFinite(sessionGRaw)
            && MathUtil.IsFinite(SessionReferenceG);
        if (!eligible)
        {
            sessionGCounter = 0;
            SetSessionGActive(false);
            return;
        }

        double deviation = Math.Abs(sessionGRaw - SessionReferenceG) / SessionReferenceG;
        sessionGCounter = deviation > SessionGDeviation
            ? Math.Min(sessionGCounter + 1, SessionGActivationCount)
            : Math.Max(0, sessionGCounter - SessionGDecayStep);

        if (!SessionGActive && sessionGCounter >= SessionGActivationCount)
        {
            SetSessionGActive(true);
        }
        else if (SessionGActive && sessionGCounter == 0)
        {
            SetSessionGActive(false);
        }
    }

    private void SetSessionGActive(bool active)
    {
        if (active == SessionGActive)
        {
            return;
        }

        SessionGActive = active;
        log.Info(string.Format(
            CultureInfo.InvariantCulture,
            active
                ? "Balance learner: session steering gain G = {0:0.00000} replaces the baseline {1:0.00000} (persistent deviation)."
                : "Balance learner: session steering gain G = {0:0.00000} agrees with the baseline {1:0.00000} again.",
            SessionG,
            SessionReferenceG));
    }

    private void PublishTheta0()
    {
        long n = baselineTheta0.Count;
        if (n >= MinSamplesTheta0)
        {
            double confidence = Math.Min(1.0, n / FullConfidenceSamplesTheta0);
            double theta0 = ClampWithPenalty(
                baselineTheta0.Value,
                -Theta0LimitRad,
                Theta0LimitRad,
                Theta0ClampToleranceRad,
                Theta0ClampToleranceRad,
                ref confidence);
            Publish(Theta0, theta0, confidence, n);
        }
        else
        {
            Publish(Theta0, double.NaN, 0.0, n);
        }
    }

    private void RefreshSessionTheta0()
    {
        SessionTheta0 = sessionTheta0.Count > 0
            ? MathUtil.Clamp(sessionTheta0.Value, -Theta0LimitRad, Theta0LimitRad)
            : double.NaN;
    }

    /// <summary>Spec 5.3: the session θ0 takes over when it differs from the reference by more than 1.5° (released below 1°).</summary>
    private void UpdateSessionTheta0Override()
    {
        if (!MathUtil.IsFinite(SessionReferenceTheta0) && IsTrusted(Theta0))
        {
            SessionReferenceTheta0 = Theta0.Value;
        }

        bool eligible = sessionTheta0.Count >= SessionMinSamples && MathUtil.IsFinite(SessionReferenceTheta0);
        bool active = SessionTheta0Active;
        if (!eligible)
        {
            active = false;
        }
        else
        {
            double difference = Math.Abs(SessionTheta0 - SessionReferenceTheta0);
            if (!active && difference > SessionTheta0OnRad)
            {
                active = true;
            }
            else if (active && difference < SessionTheta0OffRad)
            {
                active = false;
            }
        }

        if (active == SessionTheta0Active)
        {
            return;
        }

        SessionTheta0Active = active;
        log.Info(string.Format(
            CultureInfo.InvariantCulture,
            active
                ? "Balance learner: session steering offset {0:0.00}° replaces the baseline {1:0.00}°."
                : "Balance learner: session steering offset {0:0.00}° agrees with the baseline {1:0.00}° again.",
            SessionTheta0 * MathUtil.RadToDeg,
            SessionReferenceTheta0 * MathUtil.RadToDeg));
    }

    private void PublishAyMax()
    {
        long mass = ayHistogram.Total;
        if (mass >= MinSamplesAyMax)
        {
            Publish(AyMax, ayHistogram.Percentile(AyPercentile), Math.Min(1.0, mass / FullConfidenceSamplesAyMax), aySamples);
        }
        else
        {
            Publish(AyMax, double.NaN, 0.0, aySamples);
        }
    }

    private void PublishAlphaPeak()
    {
        long mass = alphaHistogram.Total;
        if (mass >= MinSamplesAlphaPeak)
        {
            Publish(AlphaPeak, alphaHistogram.Percentile(AlphaPercentile), Math.Min(1.0, mass / FullConfidenceSamplesAlphaPeak), alphaSamples);
        }
        else
        {
            Publish(AlphaPeak, double.NaN, 0.0, alphaSamples);
        }
    }

    private void PublishTau() => Publish(TauYaw, yawLag.Tau, yawLag.Confidence, yawLag.EventsTotal);

    /// <summary>EMA that starts as a plain running mean, so the first samples are not over-weighted.</summary>
    private sealed class WarmStartEma
    {
        public double Value { get; private set; }

        public long Count { get; private set; }

        public void Add(double x, double dt, double timeConstant)
        {
            Count++;
            double rate = Math.Max(1.0 / Count, MathUtil.LagAlpha(dt, timeConstant));
            Value += (x - Value) * rate;
        }

        public void Restore(double value, long count)
        {
            Value = count > 0 ? value : 0.0;
            Count = count;
        }

        public void Clear()
        {
            Value = 0.0;
            Count = 0;
        }
    }
}

/// <summary>Outcome of offering one sample to a <see cref="SteeringGainFit"/>.</summary>
internal enum FitSampleResult
{
    /// <summary>Invalid input; nothing changed.</summary>
    Ignored = 0,

    /// <summary>Implausible or an outlier: counted and folded (clipped) into the residual scale, not fitted.</summary>
    Rejected,

    /// <summary>Fitted.</summary>
    Accepted,
}

/// <summary>
/// Robust fit of the steady-state steering model <c>r_ss = G·v·θeff / (1 + K·v²)</c> in its linear form
/// (spec 5.2): <c>y = v·θeff / r = a + b·x</c> with <c>x = v²/1000</c>, <c>G = 1/a</c>, <c>K = b/(1000·a)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Weighted least squares with weight 1/ŷ² (ŷ = prediction before the update): yaw-rate noise is relative, so every
/// sample counts by its relative error and high-speed samples with large y are not over-weighted. The weight comes
/// from the prediction, never from the sample itself, so it does not bias the fit. For the same reason the weighted
/// residual sum of squares divided by the (forgetting-weighted) sample count is the mean squared relative error.
/// </para>
/// <para>
/// Outliers (spec: residual &gt; 3 × MAD): the relative a-priori residual is standardized by the fit's own
/// uncertainty at x (so a first sample at a new speed is not mistaken for an outlier) and compared with a robust
/// scale, an EMA of the clipped residual magnitudes. The gate opens after 50 residuals.
/// </para>
/// <para>
/// Speed spread for trusting K: √12 × the forgetting-weighted standard deviation of v (the range of an equally
/// spread sample). Unlike a plain min/max it forgets with the fit and ignores a single odd sample.
/// </para>
/// </remarks>
internal sealed class SteeringGainFit
{
    /// <summary>v² is divided by this so the slope b (≈ K·a·1000) has the magnitude of the intercept a (≈ 1/G ≈ 40).</summary>
    public const double SpeedSquaredScale = 1000.0;

    /// <summary>Residuals needed before the outlier gate opens.</summary>
    public const int WarmupSamples = 50;

    /// <summary>Outlier when the standardized residual exceeds this many residual scales.</summary>
    private const double OutlierThreshold = 3.0;

    /// <summary>
    /// EMA rate of the residual scale (≈ 50-sample memory). Clipped at the outlier threshold, gross outliers cannot
    /// inflate it, while a real, persistent change of the car (every residual large) still grows it by about 4 % per
    /// sample until the new data is accepted again.
    /// </summary>
    private const double ResidualScaleRate = 0.02;

    /// <summary>Floor of the scale used by the gate (0.5 %), so noise-free data does not reject its own rounding.</summary>
    private const double MinResidualScale = 0.005;

    // Plausible y = (1/G)(1 + K·v²) for G in [0.005, 0.1] and K in [0, 0.004], widened 4x: only garbage
    // (r close to 0, wrong units) falls outside.
    private const double MinPlausibleY = 2.5;
    private const double MaxPlausibleYAtRest = 800.0;
    private const double MaxPlausibleK = 0.004;

    /// <summary>Prior standard deviation of a and b (both ≈ 10..300): weak, a few samples outweigh it.</summary>
    private const double PriorStd = 100.0;

    /// <summary>Range of a uniform distribution per standard deviation.</summary>
    private static readonly double UniformRangePerStd = Math.Sqrt(12.0);

    private readonly RecursiveLeastSquares rls = new RecursiveLeastSquares(0.0, 0.0, PriorStd, PriorStd);

    private double residualScale;
    private long residualSamples;

    // Forgetting-weighted sample count, Σv and Σv² of the fitted samples (same λ as the sums of the fit).
    private double sampleWeight;
    private double speedSum;
    private double speedSquareSum;

    /// <summary>Accepted samples (lifetime of this fit).</summary>
    public long Samples { get; private set; }

    /// <summary>Rejected samples (outliers and implausible values).</summary>
    public long Rejected { get; private set; }

    /// <summary>Speed spread in m/s (0 without data).</summary>
    public double SpeedSpread
    {
        get
        {
            if (!(sampleWeight > 0))
            {
                return 0.0;
            }

            double mean = speedSum / sampleWeight;
            double variance = (speedSquareSum / sampleWeight) - (mean * mean);
            return variance > 0 ? UniformRangePerStd * Math.Sqrt(variance) : 0.0;
        }
    }

    /// <summary>Moves the prior mean of the 2-parameter solution (a, b in fit units).</summary>
    public void SetPriorMean(double a, double b) => rls.SetPriorMean(a, b);

    public void Clear()
    {
        rls.Clear();
        residualScale = 0.0;
        residualSamples = 0;
        sampleWeight = 0.0;
        speedSum = 0.0;
        speedSquareSum = 0.0;
        Samples = 0;
        Rejected = 0;
    }

    /// <summary>
    /// Restarts this fit as a down-weighted copy of <paramref name="source"/> (worth about
    /// <paramref name="equivalentSamples"/> samples, forgotten with this fit's own λ) that inherits the source's
    /// residual scale, so outliers are rejected from the first sample on. Own sample counters start at 0.
    /// </summary>
    public void SeedFrom(SteeringGainFit source, double equivalentSamples)
    {
        Clear();
        rls.SetPriorMean(source.rls.A, source.rls.B);
        if (source.rls.IsEmpty || !(source.sampleWeight > 0))
        {
            return;
        }

        double scale = Math.Min(1.0, equivalentSamples / source.sampleWeight);
        rls.CopyScaledFrom(source.rls, scale);
        sampleWeight = source.sampleWeight * scale;
        speedSum = source.speedSum * scale;
        speedSquareSum = source.speedSquareSum * scale;
        residualScale = source.residualScale;
        residualSamples = source.residualSamples;
    }

    /// <summary>Offers one sample (v in m/s, y = v·θeff/r) with forgetting factor <paramref name="lambda"/>.</summary>
    public FitSampleResult Add(double v, double y, double lambda)
    {
        if (!MathUtil.IsFinite(v) || !(v > 0) || !MathUtil.IsFinite(y) || !(lambda > 0) || lambda > 1.0)
        {
            return FitSampleResult.Ignored;
        }

        double speedSquared = v * v;
        double maxPlausibleY = MaxPlausibleYAtRest * (1.0 + (MaxPlausibleK * speedSquared));
        if (y < MinPlausibleY || y > maxPlausibleY)
        {
            Rejected++;
            return FitSampleResult.Rejected;
        }

        double x = speedSquared / SpeedSquaredScale;
        double predicted = MathUtil.Clamp(rls.Predict(x), MinPlausibleY, maxPlausibleY);
        double weight = 1.0 / (predicted * predicted);
        double relativeResidual = (y - predicted) / predicted;
        double standardized = Math.Abs(relativeResidual) / Math.Sqrt(1.0 + (weight * rls.PredictionVariance(x)));

        double gateScale = Math.Max(residualScale, MinResidualScale);
        bool outlier = residualSamples >= WarmupSamples && standardized > OutlierThreshold * gateScale;
        UpdateResidualScale(standardized, gateScale);
        if (outlier || !rls.Update(x, y, weight, lambda))
        {
            Rejected++;
            return FitSampleResult.Rejected;
        }

        sampleWeight = (lambda * sampleWeight) + 1.0;
        speedSum = (lambda * speedSum) + v;
        speedSquareSum = (lambda * speedSquareSum) + speedSquared;
        Samples++;
        return FitSampleResult.Accepted;
    }

    /// <summary>
    /// G for a fixed K (1-parameter least squares <c>y = a·(1 + K·v²)</c>) and the RMS relative error of that model.
    /// False without data.
    /// </summary>
    public bool TryGetGain(double k, out double gain, out double rmsRelativeError)
    {
        double ratio = k * SpeedSquaredScale;
        if (!rls.TrySolveWithFixedRatio(ratio, out double intercept) || !(intercept > 0))
        {
            gain = double.NaN;
            rmsRelativeError = double.NaN;
            return false;
        }

        gain = 1.0 / intercept;
        rmsRelativeError = RmsRelativeError(intercept, ratio * intercept);
        return true;
    }

    /// <summary>K of the 2-parameter fit and its RMS relative error. False without data or with a non-physical intercept.</summary>
    public bool TryGetTwoParameterFit(out double k, out double rmsRelativeError)
    {
        double intercept = rls.A;
        if (rls.IsEmpty || !(intercept > 0))
        {
            k = double.NaN;
            rmsRelativeError = double.NaN;
            return false;
        }

        k = rls.B / (intercept * SpeedSquaredScale);
        rmsRelativeError = RmsRelativeError(intercept, rls.B);
        return true;
    }

    public void SaveTo(BalanceLearnedState state)
    {
        state.FitSamples = Samples;
        state.FitRejected = Rejected;
        state.FitSumW = rls.SumW;
        state.FitSumWX = rls.SumWX;
        state.FitSumWXX = rls.SumWXX;
        state.FitSumWY = rls.SumWY;
        state.FitSumWXY = rls.SumWXY;
        state.FitSumWYY = rls.SumWYY;
        state.FitResidualScale = residualScale;
        state.FitResidualSamples = residualSamples;
        state.FitSpeedWeight = sampleWeight;
        state.FitSpeedSum = speedSum;
        state.FitSpeedSquareSum = speedSquareSum;
    }

    /// <summary>Restores a fit saved by <see cref="SaveTo"/>; invalid data leaves the fit empty and returns false.</summary>
    public bool Load(BalanceLearnedState state)
    {
        Clear();
        bool valid = state.FitSamples >= 0 && state.FitRejected >= 0 && state.FitResidualSamples >= 0
            && MathUtil.IsFinite(state.FitResidualScale) && state.FitResidualScale >= 0
            && MathUtil.IsFinite(state.FitSpeedWeight) && state.FitSpeedWeight >= 0
            && MathUtil.IsFinite(state.FitSpeedSum) && state.FitSpeedSum >= 0
            && MathUtil.IsFinite(state.FitSpeedSquareSum) && state.FitSpeedSquareSum >= 0
            && rls.TryLoad(state.FitSumW, state.FitSumWX, state.FitSumWXX, state.FitSumWY, state.FitSumWXY, state.FitSumWYY);
        if (!valid)
        {
            Clear();
            return false;
        }

        Samples = state.FitSamples;
        Rejected = state.FitRejected;
        residualScale = state.FitResidualScale;
        residualSamples = state.FitResidualSamples;
        sampleWeight = state.FitSpeedWeight;
        speedSum = state.FitSpeedSum;
        speedSquareSum = state.FitSpeedSquareSum;
        return true;
    }

    /// <summary>RMS relative error of the line (a, b) over the fitted data (NaN without data).</summary>
    private double RmsRelativeError(double intercept, double slope) =>
        sampleWeight > 0 ? Math.Sqrt(rls.ResidualSumOfSquares(intercept, slope) / sampleWeight) : double.NaN;

    private void UpdateResidualScale(double standardized, double gateScale)
    {
        residualSamples++;

        // Warm-up: plain running mean. Afterwards an EMA of residuals clipped at the outlier threshold.
        double magnitude = residualSamples > WarmupSamples ? Math.Min(standardized, OutlierThreshold * gateScale) : standardized;
        double rate = Math.Max(1.0 / residualSamples, ResidualScaleRate);
        residualScale += (magnitude - residualScale) * rate;
    }
}
