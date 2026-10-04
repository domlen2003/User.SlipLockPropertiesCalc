namespace DivebombLogistics.Haptics.Telemetry;

/// <summary>Where the base wheel-slip signal comes from.</summary>
public enum SlipSourceKind
{
    /// <summary>No slip source resolved (yet).</summary>
    None = 0,

    /// <summary>ShakeIT exported <c>WheelSlip</c>/<c>proxyS</c> (unsigned 0..100). Works for all games with a ShakeIT profile.</summary>
    ShakeIt,

    /// <summary>ACC native <c>Physics.WheelSlip0N</c> (unsigned ratio, scaled x20 to 0..100).</summary>
    AccNative,

    /// <summary>rFactor2/LMU wheel rotation vs vehicle speed (signed %, negative = locking).</summary>
    RFactorRotation,

    /// <summary>Per-wheel speed vs vehicle speed (signed %), after detection confirmed per-wheel data.</summary>
    PerWheelSpeed,
}

/// <summary>Per-wheel speed capability detection state (legacy state machine).</summary>
public enum DetectionState
{
    Loading = 0,
    Detecting,
    PerWheel,
    Mono,
}

/// <summary>Three-valued flag for capabilities that may not be known yet.</summary>
public enum TriState
{
    Unknown = 0,
    No,
    Yes,
}
