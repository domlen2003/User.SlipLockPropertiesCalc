namespace DivebombLogistics.Haptics.SlipLock;

/// <summary>
/// Per-game preprocessor tuning ("number presets"). Code-defined and intentionally not user-editable;
/// shown read-only in the debug view so future tuning can be done in code (see <see cref="GamePresets"/>).
/// Immutable.
/// </summary>
public sealed class GamePreset
{
    public GamePreset(
        double slipLat,
        double slipLong,
        double lockLat,
        double lockLong,
        double absLat,
        double absLong,
        double tcLat,
        double tcLong,
        bool synthLockFromSlip,
        double speedFadeKmh = 0,
        bool inverseSlipLoad = false,
        double preGain = 100,
        double preCut = 0)
    {
        SlipLat = slipLat;
        SlipLong = slipLong;
        LockLat = lockLat;
        LockLong = lockLong;
        AbsLat = absLat;
        AbsLong = absLong;
        TcLat = tcLat;
        TcLong = tcLong;
        SynthLockFromSlip = synthLockFromSlip;
        SpeedFadeKmh = speedFadeKmh;
        InverseSlipLoad = inverseSlipLoad;
        PreGain = preGain;
        PreCut = preCut;
    }

    /// <summary>Corner-load (proxyL) lateral influence for the slip channel, percent.</summary>
    public double SlipLat { get; }

    /// <summary>Corner-load (proxyL) longitudinal influence for the slip channel, percent.</summary>
    public double SlipLong { get; }

    public double LockLat { get; }

    public double LockLong { get; }

    public double AbsLat { get; }

    public double AbsLong { get; }

    public double TcLat { get; }

    public double TcLong { get; }

    /// <summary>
    /// True: for unsigned slip sources, lock is derived from slip while braking (brake &gt; 5 and surge &gt; 0.1).
    /// Signed sources always use the negative slip direction for lock.
    /// </summary>
    public bool SynthLockFromSlip { get; }

    /// <summary>Below this speed slip/lock fade linearly to zero (0 = no fade).</summary>
    public double SpeedFadeKmh { get; }

    /// <summary>True: divide slip by corner load instead of multiplying (counteracts load-dependent rotation slip).</summary>
    public bool InverseSlipLoad { get; }

    /// <summary>Preprocessor gain in percent (100 = unchanged).</summary>
    public double PreGain { get; }

    /// <summary>Preprocessor cut in percent: values at/below become 0, the rest is remapped to 0..100.</summary>
    public double PreCut { get; }
}
