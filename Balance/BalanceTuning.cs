using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Balance;

/// <summary>
/// Global (all cars) understeer/oversteer detector tuning. Defaults are the car-agnostic values from the
/// spec (section 4.1/4.2). Persisted inside <c>PluginSettings.Balance</c>; editable in the debug view.
/// Plain public fields: JSON-serializable and atomically readable from the data thread (doubles/bools).
/// Angles are in the unit named by the suffix (Deg); everything else is SI.
/// </summary>
public sealed class BalanceTuning
{
    public BalanceMode Mode = BalanceMode.Auto;

    // ---- Speed ramp / basic thresholds ----

    /// <summary>Below this speed (m/s) outputs are 0.</summary>
    public double VMin = 8.0;

    /// <summary>At/above this speed (m/s) the speed ramp is 1.</summary>
    public double VFull = 15.0;

    /// <summary>Minimum |theta - theta0| (steering-wheel degrees) for understeer evaluation.</summary>
    public double ThetaDeadbandDeg = 2.0;

    /// <summary>|r_ref| below this (rad/s) makes the yaw ratio undefined.</summary>
    public double RFloor = 0.05;

    /// <summary>EMA time constant (s) on theta, r, vy, v.</summary>
    public double TauInput = 0.03;

    /// <summary>Default steering→yaw lag (s) before learning.</summary>
    public double TauYawDefault = 0.12;

    // ---- Understeer (on u = 1 - rho) ----
    public double UsOnset = 0.10;
    public double UsFull = 0.45;

    // ---- Oversteer detector 1: yaw excess (on rho - 1) ----
    public double OsYawOnset = 0.08;
    public double OsYawFull = 0.40;

    // ---- Oversteer detector 2: countersteer ----
    public double CsThetaMinDeg = 3.0;
    public double CsROnset = 0.10;
    public double CsRFull = 0.60;
    public double CsBase = 0.5;

    // ---- Oversteer detector 3: body slip beyond learned envelope (asphalt margins) ----
    public double BetaOnsetMarginDeg = 1.5;
    public double BetaFullMarginDeg = 8.0;

    /// <summary>Loose-surface multiplier on beta margins and yaw-excess thresholds.</summary>
    public double LooseSurfaceMultiplier = 2.5;

    // ---- Spin ----
    public double BetaSpinDeg = 45.0;

    /// <summary>After this long (s) in a continuous spin, outputs fade to 0.</summary>
    public double SpinTimeout = 1.5;

    // ---- Output shaping ----

    /// <summary>Envelope attack time constant (s).</summary>
    public double Attack = 0.03;

    /// <summary>Envelope release time constant (s).</summary>
    public double Release = 0.15;

    /// <summary>
    /// Hysteresis in normalized output units: an output arms once its raw intensity reaches this value and
    /// disarms when the raw intensity returns to 0 (i.e. the metric falls back to its onset).
    /// </summary>
    public double Hysteresis = 0.03;

    /// <summary>Gamma applied after the linear onset→full mapping (1 = linear).</summary>
    public double Gamma = 1.0;

    /// <summary>OS above this forces US to 0 (mutual exclusion).</summary>
    public double MutualExclusionOs = 0.2;

    /// <summary>Threshold for context tags (PowerOversteer etc.).</summary>
    public double TagThreshold = 0.2;

    // ---- Edge cases ----

    /// <summary>Horizontal acceleration spike (in g) that triggers the contact blank.</summary>
    public double ContactBlankG = 4.0;

    /// <summary>Contact blank duration (s).</summary>
    public double ContactBlankTime = 0.5;

    /// <summary>Airborne when specific vertical acceleration drops below (1 - this) g.</summary>
    public double AirborneMarginG = 0.6;

    /// <summary>How long (s) outputs stay frozen/faded after an airborne sample.</summary>
    public double AirborneHold = 0.3;

    /// <summary>Blank time (s) after reset/teleport/car change/rejoin.</summary>
    public double ResetBlankTime = 2.0;

    /// <summary>No new sample for this long (s) → outputs decay to 0.</summary>
    public double FrozenTimeout = 0.2;

    /// <summary>Samples with a gap larger than this (s) are treated as a discontinuity (filters re-initialized).</summary>
    public double MaxDt = 0.1;

    // ---- Direct slip-angle path (fractions of the learned peak slip angle) ----
    public double DirectUsOnset = 0.15;
    public double DirectUsFull = 0.60;
    public double DirectOsOnset = 0.15;
    public double DirectOsFull = 0.60;

    /// <summary>Direct-path US requires the front slip angle to be at least this fraction of the learned peak.</summary>
    public double DirectPeakGate = 0.8;

    // ---- Vehicle model defaults (spec 4.2) ----

    /// <summary>Default road-wheel lock (rad) used to derive G from the steering-wheel half-lock.</summary>
    public double DeltaMaxDefaultRad = 0.44;

    public double WheelbaseDefaultM = 2.7;
    public double SteeringRatioDefault = 14.0;

    /// <summary>G fallback (1/m) when half-lock is unknown (≈ ratio 14, wheelbase 2.7 m).</summary>
    public double GDefault = 0.026;

    /// <summary>K fallback (s²/m²) ≈ 2°/g understeer gradient.</summary>
    public double KDefault = 0.0013;

    // ---- Learning ----

    /// <summary>Learned values are used only at/above this confidence.</summary>
    public double LearnedConfidenceThreshold = 0.6;

    /// <summary>RLS forgetting factor for the persisted baseline.</summary>
    public double RlsLambda = 0.9995;

    /// <summary>RLS forgetting factor for the per-session adaptation layer.</summary>
    public double SessionRlsLambda = 0.995;

    /// <summary>No learning when track wetness (0..1) is above this.</summary>
    public double WetnessLearningMax = 0.3;

    /// <summary>Restores every field to its default.</summary>
    public void ResetToDefaults() => new BalanceTuning().CopyTo(this);

    /// <summary>Copies all values into <paramref name="target"/>.</summary>
    public void CopyTo(BalanceTuning target)
    {
        target.Mode = Mode;
        target.VMin = VMin;
        target.VFull = VFull;
        target.ThetaDeadbandDeg = ThetaDeadbandDeg;
        target.RFloor = RFloor;
        target.TauInput = TauInput;
        target.TauYawDefault = TauYawDefault;
        target.UsOnset = UsOnset;
        target.UsFull = UsFull;
        target.OsYawOnset = OsYawOnset;
        target.OsYawFull = OsYawFull;
        target.CsThetaMinDeg = CsThetaMinDeg;
        target.CsROnset = CsROnset;
        target.CsRFull = CsRFull;
        target.CsBase = CsBase;
        target.BetaOnsetMarginDeg = BetaOnsetMarginDeg;
        target.BetaFullMarginDeg = BetaFullMarginDeg;
        target.LooseSurfaceMultiplier = LooseSurfaceMultiplier;
        target.BetaSpinDeg = BetaSpinDeg;
        target.SpinTimeout = SpinTimeout;
        target.Attack = Attack;
        target.Release = Release;
        target.Hysteresis = Hysteresis;
        target.Gamma = Gamma;
        target.MutualExclusionOs = MutualExclusionOs;
        target.TagThreshold = TagThreshold;
        target.ContactBlankG = ContactBlankG;
        target.ContactBlankTime = ContactBlankTime;
        target.AirborneMarginG = AirborneMarginG;
        target.AirborneHold = AirborneHold;
        target.ResetBlankTime = ResetBlankTime;
        target.FrozenTimeout = FrozenTimeout;
        target.MaxDt = MaxDt;
        target.DirectUsOnset = DirectUsOnset;
        target.DirectUsFull = DirectUsFull;
        target.DirectOsOnset = DirectOsOnset;
        target.DirectOsFull = DirectOsFull;
        target.DirectPeakGate = DirectPeakGate;
        target.DeltaMaxDefaultRad = DeltaMaxDefaultRad;
        target.WheelbaseDefaultM = WheelbaseDefaultM;
        target.SteeringRatioDefault = SteeringRatioDefault;
        target.GDefault = GDefault;
        target.KDefault = KDefault;
        target.LearnedConfidenceThreshold = LearnedConfidenceThreshold;
        target.RlsLambda = RlsLambda;
        target.SessionRlsLambda = SessionRlsLambda;
        target.WetnessLearningMax = WetnessLearningMax;
    }

    /// <summary>Repairs out-of-range or non-finite values after deserialization (falls back to defaults).</summary>
    public void Normalize()
    {
        var d = new BalanceTuning();
        VMin = Fix(VMin, d.VMin, 0, 100);
        VFull = Fix(VFull, d.VFull, VMin + 0.1, 150);
        ThetaDeadbandDeg = Fix(ThetaDeadbandDeg, d.ThetaDeadbandDeg, 0, 45);
        RFloor = Fix(RFloor, d.RFloor, 0.001, 2);
        TauInput = Fix(TauInput, d.TauInput, 0, 1);
        TauYawDefault = Fix(TauYawDefault, d.TauYawDefault, 0.01, 1);
        UsOnset = Fix(UsOnset, d.UsOnset, 0, 1);
        UsFull = Fix(UsFull, d.UsFull, UsOnset + 0.01, 2);
        OsYawOnset = Fix(OsYawOnset, d.OsYawOnset, 0, 2);
        OsYawFull = Fix(OsYawFull, d.OsYawFull, OsYawOnset + 0.01, 5);
        CsThetaMinDeg = Fix(CsThetaMinDeg, d.CsThetaMinDeg, 0, 90);
        CsROnset = Fix(CsROnset, d.CsROnset, 0, 3);
        CsRFull = Fix(CsRFull, d.CsRFull, CsROnset + 0.01, 5);
        CsBase = Fix(CsBase, d.CsBase, 0, 1);
        BetaOnsetMarginDeg = Fix(BetaOnsetMarginDeg, d.BetaOnsetMarginDeg, 0, 45);
        BetaFullMarginDeg = Fix(BetaFullMarginDeg, d.BetaFullMarginDeg, BetaOnsetMarginDeg + 0.1, 90);
        LooseSurfaceMultiplier = Fix(LooseSurfaceMultiplier, d.LooseSurfaceMultiplier, 1, 10);
        BetaSpinDeg = Fix(BetaSpinDeg, d.BetaSpinDeg, 5, 180);
        SpinTimeout = Fix(SpinTimeout, d.SpinTimeout, 0, 10);
        Attack = Fix(Attack, d.Attack, 0, 2);
        Release = Fix(Release, d.Release, 0, 5);
        Hysteresis = Fix(Hysteresis, d.Hysteresis, 0, 0.5);
        Gamma = Fix(Gamma, d.Gamma, 0.1, 5);
        MutualExclusionOs = Fix(MutualExclusionOs, d.MutualExclusionOs, 0, 1);
        TagThreshold = Fix(TagThreshold, d.TagThreshold, 0, 1);
        ContactBlankG = Fix(ContactBlankG, d.ContactBlankG, 1, 50);
        ContactBlankTime = Fix(ContactBlankTime, d.ContactBlankTime, 0, 10);
        AirborneMarginG = Fix(AirborneMarginG, d.AirborneMarginG, 0.1, 1);
        AirborneHold = Fix(AirborneHold, d.AirborneHold, 0, 5);
        ResetBlankTime = Fix(ResetBlankTime, d.ResetBlankTime, 0, 30);
        FrozenTimeout = Fix(FrozenTimeout, d.FrozenTimeout, 0.02, 10);
        // Must stay above the slowest telemetry interval (LMU 50 Hz = 0.02 s), otherwise every sample is a discontinuity.
        MaxDt = Fix(MaxDt, d.MaxDt, 0.05, 1);
        DirectUsOnset = Fix(DirectUsOnset, d.DirectUsOnset, 0, 5);
        DirectUsFull = Fix(DirectUsFull, d.DirectUsFull, DirectUsOnset + 0.01, 10);
        DirectOsOnset = Fix(DirectOsOnset, d.DirectOsOnset, 0, 5);
        DirectOsFull = Fix(DirectOsFull, d.DirectOsFull, DirectOsOnset + 0.01, 10);
        DirectPeakGate = Fix(DirectPeakGate, d.DirectPeakGate, 0, 2);
        DeltaMaxDefaultRad = Fix(DeltaMaxDefaultRad, d.DeltaMaxDefaultRad, 0.05, 1.5);
        WheelbaseDefaultM = Fix(WheelbaseDefaultM, d.WheelbaseDefaultM, 1, 6);
        SteeringRatioDefault = Fix(SteeringRatioDefault, d.SteeringRatioDefault, 1, 40);
        GDefault = Fix(GDefault, d.GDefault, 0.001, 1);
        KDefault = Fix(KDefault, d.KDefault, 0, 0.05);
        LearnedConfidenceThreshold = Fix(LearnedConfidenceThreshold, d.LearnedConfidenceThreshold, 0, 1);
        RlsLambda = Fix(RlsLambda, d.RlsLambda, 0.9, 1);
        SessionRlsLambda = Fix(SessionRlsLambda, d.SessionRlsLambda, 0.9, 1);
        WetnessLearningMax = Fix(WetnessLearningMax, d.WetnessLearningMax, 0, 1);
    }

    private static double Fix(double value, double fallback, double min, double max)
    {
        if (!MathUtil.IsFinite(value) || value < min || value > max)
        {
            return MathUtil.Clamp(fallback, min, max);
        }

        return value;
    }
}
