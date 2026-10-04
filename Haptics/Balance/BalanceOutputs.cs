namespace DivebombLogistics.Haptics.Balance;

/// <summary>
/// Outputs of the understeer/oversteer estimator for one tick. Exported as <c>Balance.*</c> SimHub properties
/// (see spec section 7) and mirrored to the debug view. Angles are in degrees where suffixed "Deg".
/// Unknown/undefined debug values are NaN (the exporter maps NaN to 0).
/// </summary>
public sealed class BalanceOutputs
{
    // ---- Outputs (exported) ----

    /// <summary>Understeer intensity 0..1 (shaped).</summary>
    public double Understeer;

    /// <summary>Oversteer intensity 0..1 (shaped).</summary>
    public double Oversteer;

    public bool PowerOversteer;
    public bool LiftOrBrakeOversteer;
    public bool EntryUndersteer;
    public bool ExitUndersteer;
    public bool Countersteer;
    public bool Spin;

    /// <summary>All gates open (see <see cref="Gate"/>).</summary>
    public bool Active;

    /// <summary>Confidence 0..1 in the vehicle model / direct path currently used.</summary>
    public double Confidence;

    // ---- State ----
    public BalanceGate Gate = BalanceGate.NoData;
    public BalancePath Path;

    // ---- Debug: signals ----
    public double YawRate = double.NaN;
    public double YawRef = double.NaN;
    public double YawRatio = double.NaN;
    public double BodySlipDeg = double.NaN;
    public double SteeringDeg = double.NaN;
    public double SteeringEffDeg = double.NaN;
    public double SpeedRamp;

    /// <summary>Raw (pre-shaping) detector intensities 0..1.</summary>
    public double UsModel;
    public double OsModel;
    public double UsDirect;
    public double OsDirect;
    public double OsYaw;
    public double OsCountersteer;
    public double OsBodySlip;

    /// <summary>Mean |front| / |rear| slip angle (degrees when the source reports radians, else native unit).</summary>
    public double AlphaFront = double.NaN;
    public double AlphaRear = double.NaN;

    // ---- Debug: effective parameters ----
    public double G = double.NaN;
    public double K = double.NaN;

    /// <summary>Steering offset in steering-wheel degrees.</summary>
    public double Theta0Deg = double.NaN;

    public double TauYaw = double.NaN;
    public double AyMax = double.NaN;
    public double AlphaPeak = double.NaN;

    public ParamSource GSource;
    public ParamSource KSource;
    public ParamSource Theta0Source;
    public ParamSource TauSource;

    public double ConfG;
    public double ConfK;

    public long SamplesG;
    public long SamplesK;
    public long SamplesTheta0;
    public long SamplesTau;
    public long SamplesAy;
    public long SamplesAlpha;

    /// <summary>Car class preset in effect (manual override or auto-detected).</summary>
    public BalanceClassPreset ClassPreset;

    /// <summary>True when this tick fed the learner.</summary>
    public bool LearningActive;

    public bool LearningLocked;
    public bool SessionOverrideActive;

    /// <summary>Runtime-verified sign corrections (+1 / -1).</summary>
    public int SteeringSign = 1;
    public int ForwardSign = 1;

    /// <summary>False while the steering sign of the sim is being verified (gate <see cref="BalanceGate.Calibrating"/>).</summary>
    public bool SteeringSignVerified;

    /// <summary>Null until auto-detected.</summary>
    public bool? GravityIncluded;

    /// <summary>Initial values, copied by <see cref="Clear"/> (so clearing never allocates, also in error paths).</summary>
    private static readonly BalanceOutputs Initial = new BalanceOutputs();

    /// <summary>Resets every field to its initial value (allocation-free).</summary>
    public void Clear() => Initial.CopyTo(this);

    /// <summary>Copies all fields into <paramref name="t"/> (allocation-free).</summary>
    public void CopyTo(BalanceOutputs t)
    {
        t.Understeer = Understeer;
        t.Oversteer = Oversteer;
        t.PowerOversteer = PowerOversteer;
        t.LiftOrBrakeOversteer = LiftOrBrakeOversteer;
        t.EntryUndersteer = EntryUndersteer;
        t.ExitUndersteer = ExitUndersteer;
        t.Countersteer = Countersteer;
        t.Spin = Spin;
        t.Active = Active;
        t.Confidence = Confidence;
        t.Gate = Gate;
        t.Path = Path;
        t.YawRate = YawRate;
        t.YawRef = YawRef;
        t.YawRatio = YawRatio;
        t.BodySlipDeg = BodySlipDeg;
        t.SteeringDeg = SteeringDeg;
        t.SteeringEffDeg = SteeringEffDeg;
        t.SpeedRamp = SpeedRamp;
        t.UsModel = UsModel;
        t.OsModel = OsModel;
        t.UsDirect = UsDirect;
        t.OsDirect = OsDirect;
        t.OsYaw = OsYaw;
        t.OsCountersteer = OsCountersteer;
        t.OsBodySlip = OsBodySlip;
        t.AlphaFront = AlphaFront;
        t.AlphaRear = AlphaRear;
        t.G = G;
        t.K = K;
        t.Theta0Deg = Theta0Deg;
        t.TauYaw = TauYaw;
        t.AyMax = AyMax;
        t.AlphaPeak = AlphaPeak;
        t.GSource = GSource;
        t.KSource = KSource;
        t.Theta0Source = Theta0Source;
        t.TauSource = TauSource;
        t.ConfG = ConfG;
        t.ConfK = ConfK;
        t.SamplesG = SamplesG;
        t.SamplesK = SamplesK;
        t.SamplesTheta0 = SamplesTheta0;
        t.SamplesTau = SamplesTau;
        t.SamplesAy = SamplesAy;
        t.SamplesAlpha = SamplesAlpha;
        t.ClassPreset = ClassPreset;
        t.LearningActive = LearningActive;
        t.LearningLocked = LearningLocked;
        t.SessionOverrideActive = SessionOverrideActive;
        t.SteeringSign = SteeringSign;
        t.ForwardSign = ForwardSign;
        t.SteeringSignVerified = SteeringSignVerified;
        t.GravityIncluded = GravityIncluded;
    }
}
