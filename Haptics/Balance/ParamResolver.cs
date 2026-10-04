using System;
using DivebombLogistics.Core;

namespace DivebombLogistics.Haptics.Balance;

/// <summary>
/// Vehicle-model parameters the estimator uses for one car, after applying the per-parameter precedence
/// Manual &gt; Session &gt; Learned (confidence ≥ threshold) &gt; Preset &gt; Default (spec 5.1).
/// Filled in place by <see cref="ParamResolver.Resolve"/>; initialized to the car-agnostic defaults.
/// </summary>
internal sealed class EffectiveParams
{
    public EffectiveParams()
    {
        var defaults = new BalanceTuning();
        G = defaults.GDefault;
        K = defaults.KDefault;
        TauYaw = defaults.TauYawDefault;
        BetaOnsetMarginDeg = defaults.BetaOnsetMarginDeg;
        BetaFullMarginDeg = defaults.BetaFullMarginDeg;
    }

    /// <summary>Steering gain G = 1 / (steering ratio · wheelbase) in 1/m.</summary>
    public double G;

    /// <summary>Understeer factor K in s²/m².</summary>
    public double K;

    /// <summary>Steering offset in steering-wheel radians.</summary>
    public double Theta0;

    /// <summary>Steering→yaw lag in seconds.</summary>
    public double TauYaw;

    /// <summary>Learned lateral acceleration limit (m/s²); NaN if not learned.</summary>
    public double AyMax = double.NaN;

    /// <summary>Learned near-peak slip angle (sim-native unit); NaN if not learned.</summary>
    public double AlphaPeak = double.NaN;

    /// <summary>Body-slip detector margins (deg) after the class preset and its multiplier.</summary>
    public double BetaOnsetMarginDeg;

    public double BetaFullMarginDeg;

    public ParamSource GSource;
    public ParamSource KSource;
    public ParamSource Theta0Source;
    public ParamSource TauSource;

    /// <summary>Learner confidences (0 without a learner), whatever source was chosen.</summary>
    public double ConfG;

    public double ConfK;
    public double ConfAlphaPeak;

    /// <summary>Class preset in effect (manual override, else auto-detected).</summary>
    public BalanceClassPreset ClassPreset;

    /// <summary>Class preset auto-detected from the car class/name (shown as "Auto (…)" in the UI).</summary>
    public BalanceClassPreset AutoClassPreset;

    public bool AlphaPeakLearned => MathUtil.IsFinite(AlphaPeak);

    public bool AyMaxLearned => MathUtil.IsFinite(AyMax);
}

/// <summary>
/// Resolves <see cref="EffectiveParams"/> from manual overrides, the learner (session layer and baseline), the car's
/// class preset and the global defaults (spec 5.1, 4.2, 4.3). Allocation-free per call: the class-preset detection,
/// which allocates, is cached per car class/name.
/// </summary>
internal sealed class ParamResolver
{
    // Sanity ranges for manual overrides (UI input); values outside are clamped, non-finite ones ignored.
    private const double ManualGMin = 0.001;
    private const double ManualGMax = 1.0;
    private const double ManualKMax = 0.05;
    private const double ManualSteeringRatioMin = 1.0;
    private const double ManualSteeringRatioMax = 40.0;
    private const double ManualWheelbaseMin = 1.0;
    private const double ManualWheelbaseMax = 6.0;
    private const double ManualTheta0MaxDeg = 45.0;
    private const double ManualTauMin = 0.01;
    private const double ManualTauMax = 1.0;

    /// <summary>Plausible range (spec 5.2) for G derived from heuristics (steering lock, default ratio).</summary>
    private const double DerivedGMin = 0.005;

    private const double DerivedGMax = 0.1;

    private readonly BalanceTuning tuning;

    private bool hasDetection;
    private string detectedCarClass;
    private string detectedCarKey;
    private BalanceClassPreset detectedPreset;

    /// <param name="tuning">Live global tuning (defaults and the learned-confidence threshold are read on every call).</param>
    public ParamResolver(BalanceTuning tuning)
    {
        this.tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
    }

    /// <summary>
    /// Fills <paramref name="result"/>. <paramref name="overrides"/> and <paramref name="learner"/> may be null.
    /// <paramref name="carClass"/>/<paramref name="carKey"/> drive the class-preset auto-detection (used unless
    /// <c>overrides.ClassPreset</c> is set); <paramref name="thetaMax"/> is the steering-wheel half-lock in radians
    /// (NaN when unknown). Also hands the non-learned K to <see cref="BalanceLearner.SetKPrior(double, bool)"/> so the
    /// learner fits G with the K the model will use.
    /// </summary>
    public void Resolve(BalanceOverrides overrides, BalanceLearner learner, string carClass, string carKey, double thetaMax, EffectiveParams result)
    {
        double threshold = tuning.LearnedConfidenceThreshold;
        BalanceClassPreset autoPreset = DetectClassPreset(carClass, carKey);
        BalanceClassPreset preset = overrides?.ClassPreset ?? autoPreset;
        ClassPresetValues presetValues = ClassPresets.Get(preset);
        result.ClassPreset = preset;
        result.AutoClassPreset = autoPreset;

        ResolveK(overrides, learner, presetValues, threshold, result);
        ResolveG(overrides, learner, presetValues, thetaMax, threshold, result);
        ResolveTheta0(overrides, learner, threshold, result);
        ResolveTau(overrides, learner, threshold, result);

        result.AyMax = learner != null ? learner.AyMax.Value : double.NaN;
        result.AlphaPeak = learner != null ? learner.AlphaPeak.Value : double.NaN;
        result.ConfG = learner != null ? learner.G.Confidence : 0.0;
        result.ConfK = learner != null ? learner.K.Confidence : 0.0;
        result.ConfAlphaPeak = learner != null ? learner.AlphaPeak.Confidence : 0.0;

        double multiplier = presetValues != null && presetValues.BetaMarginMultiplier > 0 ? presetValues.BetaMarginMultiplier : 1.0;
        result.BetaOnsetMarginDeg = PresetOr(presetValues?.BetaOnsetMarginDeg, tuning.BetaOnsetMarginDeg) * multiplier;
        result.BetaFullMarginDeg = PresetOr(presetValues?.BetaFullMarginDeg, tuning.BetaFullMarginDeg) * multiplier;
    }

    private static bool IsTrusted(LearnedParam param, double threshold) =>
        MathUtil.IsFinite(param.Value) && param.Confidence >= threshold;

    /// <summary>Finite override clamped to [min, max]; null when absent or not finite.</summary>
    private static double? Manual(double? value, double min, double max) =>
        value.HasValue && MathUtil.IsFinite(value.Value) ? MathUtil.Clamp(value.Value, min, max) : (double?)null;

    private static double PresetOr(double? presetValue, double fallback) =>
        presetValue.HasValue && MathUtil.IsFinite(presetValue.Value) ? presetValue.Value : fallback;

    private static void Set(ref double target, ref ParamSource source, double value, ParamSource from)
    {
        target = value;
        source = from;
    }

    /// <summary>
    /// K: manual, learned (trusted), class preset, default. Known deviation from spec 6 ("wet: K from the session
    /// layer"): there is no session K source. The session fit's speed spread within one session is usually too
    /// small for a two-parameter fit, so a changed balance in the wet shows up in the session G instead.
    /// </summary>
    private void ResolveK(BalanceOverrides overrides, BalanceLearner learner, ClassPresetValues presetValues, double threshold, EffectiveParams result)
    {
        double? manual = Manual(overrides?.K, 0.0, ManualKMax);
        double presetK = presetValues != null ? presetValues.K : double.NaN;

        // The best K that does not come from learning is also the prior of the learner's 1-parameter G estimate.
        double nonLearned;
        ParamSource nonLearnedSource;
        if (manual.HasValue)
        {
            nonLearned = manual.Value;
            nonLearnedSource = ParamSource.Manual;
        }
        else if (MathUtil.IsFinite(presetK) && presetK >= 0)
        {
            nonLearned = presetK;
            nonLearnedSource = ParamSource.Preset;
        }
        else
        {
            nonLearned = tuning.KDefault;
            nonLearnedSource = ParamSource.Default;
        }

        learner?.SetKPrior(nonLearned, manual.HasValue);

        if (!manual.HasValue && learner != null && IsTrusted(learner.K, threshold))
        {
            Set(ref result.K, ref result.KSource, learner.K.Value, ParamSource.Learned);
        }
        else
        {
            Set(ref result.K, ref result.KSource, nonLearned, nonLearnedSource);
        }
    }

    private void ResolveG(BalanceOverrides overrides, BalanceLearner learner, ClassPresetValues presetValues, double thetaMax, double threshold, EffectiveParams result)
    {
        double? manualG = Manual(overrides?.G, ManualGMin, ManualGMax);
        double? manualRatio = Manual(overrides?.SteeringRatio, ManualSteeringRatioMin, ManualSteeringRatioMax);
        double? manualWheelbase = Manual(overrides?.WheelbaseM, ManualWheelbaseMin, ManualWheelbaseMax);
        double presetWheelbase = presetValues != null ? presetValues.WheelbaseM : double.NaN;
        bool presetHasWheelbase = MathUtil.IsFinite(presetWheelbase) && presetWheelbase > 0;

        // A wheelbase on its own is a car-specific starting value, not a manual G: learning still wins over it.
        bool carSpecificWheelbase = manualWheelbase.HasValue || presetHasWheelbase;
        double wheelbase = manualWheelbase ?? (presetHasWheelbase ? presetWheelbase : tuning.WheelbaseDefaultM);

        if (manualG.HasValue)
        {
            Set(ref result.G, ref result.GSource, manualG.Value, ParamSource.Manual);
        }
        else if (manualRatio.HasValue)
        {
            // Known steering ratio (with the entered, preset or default wheelbase): G = 1 / (i_s · L).
            Set(ref result.G, ref result.GSource, 1.0 / (manualRatio.Value * wheelbase), ParamSource.Manual);
        }
        else if (learner != null && learner.SessionGActive && MathUtil.IsFinite(learner.SessionG))
        {
            Set(ref result.G, ref result.GSource, learner.SessionG, ParamSource.Session);
        }
        else if (learner != null && IsTrusted(learner.G, threshold))
        {
            Set(ref result.G, ref result.GSource, learner.G.Value, ParamSource.Learned);
        }
        else
        {
            // Spec 4.2: prefer the car's own steering lock (G = δ_max / (θ_max · L)), else the default ratio.
            double derived;
            if (MathUtil.IsFinite(thetaMax) && thetaMax > 0)
            {
                derived = MathUtil.Clamp(tuning.DeltaMaxDefaultRad / (thetaMax * wheelbase), DerivedGMin, DerivedGMax);
            }
            else if (carSpecificWheelbase)
            {
                derived = 1.0 / (tuning.SteeringRatioDefault * wheelbase);
            }
            else
            {
                derived = tuning.GDefault;
            }

            Set(ref result.G, ref result.GSource, derived, carSpecificWheelbase ? ParamSource.Preset : ParamSource.Default);
        }
    }

    private static void ResolveTheta0(BalanceOverrides overrides, BalanceLearner learner, double threshold, EffectiveParams result)
    {
        double? manualDeg = Manual(overrides?.Theta0Deg, -ManualTheta0MaxDeg, ManualTheta0MaxDeg);
        if (manualDeg.HasValue)
        {
            Set(ref result.Theta0, ref result.Theta0Source, manualDeg.Value * MathUtil.DegToRad, ParamSource.Manual);
        }
        else if (learner != null && learner.SessionTheta0Active && MathUtil.IsFinite(learner.SessionTheta0))
        {
            Set(ref result.Theta0, ref result.Theta0Source, learner.SessionTheta0, ParamSource.Session);
        }
        else if (learner != null && IsTrusted(learner.Theta0, threshold))
        {
            Set(ref result.Theta0, ref result.Theta0Source, learner.Theta0.Value, ParamSource.Learned);
        }
        else
        {
            Set(ref result.Theta0, ref result.Theta0Source, 0.0, ParamSource.Default);
        }
    }

    private void ResolveTau(BalanceOverrides overrides, BalanceLearner learner, double threshold, EffectiveParams result)
    {
        double? manual = Manual(overrides?.TauYawS, ManualTauMin, ManualTauMax);
        if (manual.HasValue)
        {
            Set(ref result.TauYaw, ref result.TauSource, manual.Value, ParamSource.Manual);
        }
        else if (learner != null && IsTrusted(learner.TauYaw, threshold))
        {
            Set(ref result.TauYaw, ref result.TauSource, learner.TauYaw.Value, ParamSource.Learned);
        }
        else
        {
            Set(ref result.TauYaw, ref result.TauSource, tuning.TauYawDefault, ParamSource.Default);
        }
    }

    /// <summary>Cached <see cref="ClassPresets.FromCarClass"/>: it allocates, so it runs only when the car changes.</summary>
    private BalanceClassPreset DetectClassPreset(string carClass, string carKey)
    {
        if (!hasDetection
            || !string.Equals(carClass, detectedCarClass, StringComparison.Ordinal)
            || !string.Equals(carKey, detectedCarKey, StringComparison.Ordinal))
        {
            detectedPreset = ClassPresets.FromCarClass(carClass, carKey);
            detectedCarClass = carClass;
            detectedCarKey = carKey;
            hasDetection = true;
        }

        return detectedPreset;
    }
}
