namespace User.SlipLockPropertiesCalc.Balance;

/// <summary>Estimation path selection.</summary>
public enum BalanceMode
{
    /// <summary>Direct slip-angle path when available and learned, bicycle-model path otherwise.</summary>
    Auto = 0,

    /// <summary>Always use the yaw-rate bicycle-model path.</summary>
    ModelOnly,

    /// <summary>Only the direct front/rear slip-angle path (outputs 0 where unavailable).</summary>
    DirectOnly,
}

/// <summary>Which path produced the current outputs.</summary>
public enum BalancePath
{
    None = 0,
    Model,
    Direct,
}

/// <summary>Why outputs are (or are not) active. <see cref="Active"/> means all gates are open.</summary>
public enum BalanceGate
{
    Active = 0,
    NoData,
    UnsupportedSim,
    NotOnTrack,
    Replay,
    Paused,
    PitLane,
    Reverse,
    LowSpeed,
    Blanked,
    Contact,
    Airborne,
    Frozen,
    SpinTimeout,

    /// <summary>
    /// The steering sign of this sim is not verified yet (first drive in a sim, or after a reset of the sign
    /// calibration). The model-path understeer/oversteer and the countersteer detector are suppressed because an
    /// inverted steering sign would turn every corner into countersteer; body-slip and spin detection still output.
    /// Not counted as <see cref="Active"/>. Reported only while the model path is in use (the direct slip-angle
    /// path does not depend on the steering sign).
    /// </summary>
    Calibrating,
}

/// <summary>Where an effective model parameter came from (precedence: Manual &gt; Session &gt; Learned &gt; Preset &gt; Default).</summary>
public enum ParamSource
{
    Default = 0,
    Preset,
    Learned,
    Session,
    Manual,
}

/// <summary>Surface under the car.</summary>
public enum SurfaceKind
{
    /// <summary>Not reported by the sim (treated as asphalt for gating and learning).</summary>
    Unknown = 0,
    Asphalt,
    Kerb,
    Wet,
    Loose,
}

/// <summary>Car class presets from spec section 4.3.</summary>
public enum BalanceClassPreset
{
    None = 0,
    FormulaPrototype,
    GT,
    RoadTouring,
    RallyLoose,
    Oval,
}

/// <summary>Per-car sensitivity sliders (simple view).</summary>
public enum SensitivityKind
{
    Slip = 0,
    Lock,
    Understeer,
    Oversteer,
}

/// <summary>Per-car manual overrides of vehicle-model parameters (debug view).</summary>
public enum BalanceOverrideKind
{
    /// <summary>Steering ratio (steering-wheel angle / road-wheel angle). With wheelbase derives G.</summary>
    SteeringRatio = 0,

    /// <summary>Wheelbase in metres. With steering ratio derives G.</summary>
    WheelbaseM,

    /// <summary>Combined gain G = 1 / (steering ratio · wheelbase) in 1/m (takes precedence over ratio+wheelbase).</summary>
    G,

    /// <summary>Understeer factor K in s²/m².</summary>
    K,

    /// <summary>Steering offset in steering-wheel degrees.</summary>
    Theta0Deg,

    /// <summary>Steering→yaw lag time constant in seconds.</summary>
    TauYawS,
}
