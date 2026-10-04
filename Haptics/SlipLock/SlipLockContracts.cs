using DivebombLogistics.Core;

namespace DivebombLogistics.Haptics.SlipLock;

/// <summary>
/// Per-frame inputs for <c>SlipLockProcessor</c>. One instance is reused every frame; the shell fills it.
/// </summary>
internal sealed class SlipLockInputs
{
    /// <summary>Monotonic wall-clock seconds; the processor derives its envelope dt from this.</summary>
    public double WallTime;

    /// <summary>Throttle 0..100.</summary>
    public double Throttle;

    /// <summary>Brake 0..100.</summary>
    public double Brake;

    /// <summary>SimHub AccelerationSway.</summary>
    public double Sway;

    /// <summary>SimHub AccelerationSurge (positive under braking).</summary>
    public double Surge;

    public double SpeedKmh;

    public bool AbsActive;
    public bool TcActive;

    /// <summary>True when <c>DataCorePlugin.GameData.TCLevel</c> exists (legacy "game exports TC").</summary>
    public bool GameExportsTc;

    /// <summary>True when <c>DataCorePlugin.GameData.ABSLevel</c> exists.</summary>
    public bool GameExportsAbs;

    /// <summary>TC level (-1 when not exported).</summary>
    public double TcLevel = -1;

    /// <summary>ABS level (-1 when not exported).</summary>
    public double AbsLevel = -1;

    /// <summary>Raw base slip per wheel from the resolved source (see <see cref="BaseSlipIsSigned"/>).</summary>
    public readonly double[] BaseSlip = new double[Wheels.Count];

    /// <summary>
    /// True for signed sources (rFactor rotation, per-wheel speed): positive = spin, negative = lock.
    /// False for unsigned sources (ShakeIT, ACC native) where lock must be synthesized.
    /// </summary>
    public bool BaseSlipIsSigned;

    /// <summary>ShakeIT <c>WheelLock</c> export per wheel (0..100). Valid only when <see cref="HasShakeItLock"/>.</summary>
    public readonly double[] ShakeItLock = new double[Wheels.Count];

    public bool HasShakeItLock;
}

/// <summary>
/// Global post-processor settings (the v1 sliders; now in the debug view). Copied from
/// <c>HapticsSettings</c> each frame by the shell. Units as in v1 (percent / milliseconds).
/// </summary>
internal sealed class SlipLockTuning
{
    public double SlipThrottleBlend = 20.0;
    public double TcThrottleBlend = 50.0;
    public double LockBrakeBlend = 20.0;
    public double AbsBrakeBlend = 50.0;

    public double SlipThreshold = 5.0;
    public double LockThreshold = 5.0;
    public double TcThreshold = 5.0;
    public double AbsThreshold = 5.0;

    public bool GateSlipOnThrottle;
    public bool GateLockOnBrake;

    public double SlipAttackMs = 10;
    public double SlipReleaseMs = 100;
    public double LockAttackMs = 10;
    public double LockReleaseMs = 100;
    public double AbsAttackMs = 5;
    public double AbsReleaseMs = 50;
    public double TcAttackMs = 5;
    public double TcReleaseMs = 50;

    /// <summary>New in v2: merge ShakeIT WheelLock into the lock channel (max of both).</summary>
    public bool UseShakeItWheelLock = true;

    /// <summary>Per-car slip sensitivity as a factor (1.0 = 100% = v1 behavior).</summary>
    public double SlipSensitivity = 1.0;

    /// <summary>Per-car lock sensitivity as a factor (1.0 = 100% = v1 behavior).</summary>
    public double LockSensitivity = 1.0;
}

/// <summary>
/// Results of one <c>SlipLockProcessor.Process</c> call. All arrays are length 4 (FL, FR, RL, RR) and hold the
/// exact values exported as SimHub properties (already rounded to 1 decimal like v1).
/// </summary>
internal sealed class SlipLockOutputs
{
    // ---- Exported (property name suffix in comments) ----

    /// <summary>SlipLock.Slip.* — preprocessor output (after load, fade, precut), rounded.</summary>
    public readonly double[] Slip = new double[Wheels.Count];

    /// <summary>SlipLock.Lock.*</summary>
    public readonly double[] Lock = new double[Wheels.Count];

    /// <summary>SlipLock.ABS.*</summary>
    public readonly double[] Abs = new double[Wheels.Count];

    /// <summary>SlipLock.TC.*</summary>
    public readonly double[] Tc = new double[Wheels.Count];

    /// <summary>SlipLock.SlipBlend.* — post threshold/gate/envelope, throttle-blended.</summary>
    public readonly double[] SlipBlend = new double[Wheels.Count];

    /// <summary>SlipLock.LockBlend.*</summary>
    public readonly double[] LockBlend = new double[Wheels.Count];

    /// <summary>SlipLock.SlipTC.* — aggregate: TC when the car's TC is on, else SlipBlend.</summary>
    public readonly double[] SlipTc = new double[Wheels.Count];

    /// <summary>SlipLock.LockABS.* — aggregate: ABS when the car's ABS is on, else LockBlend.</summary>
    public readonly double[] LockAbs = new double[Wheels.Count];

    public double SlipMono;
    public double LockMono;
    public double AbsMono;
    public double TcMono;
    public double SlipBlendMono;
    public double LockBlendMono;
    public double SlipTcMono;
    public double LockAbsMono;

    // ---- Debug-only ----

    /// <summary>Raw base slip as received (after the source/detector), before any processing.</summary>
    public readonly double[] BaseSlip = new double[Wheels.Count];

    /// <summary>Corner loads (proxyL, 0..50, neutral 25) used for the slip channel.</summary>
    public readonly double[] Loads = new double[Wheels.Count];

    public double BaseSlipMono;
    public double BaseLockMono;
    public double BaseAbsMono;
    public double BaseTcMono;

    /// <summary>True when the SlipTC aggregate uses TC (game exports TC and level &gt; 0).</summary>
    public bool AggregateUsesTc;

    /// <summary>True when the LockABS aggregate uses ABS.</summary>
    public bool AggregateUsesAbs;

    /// <summary>Zeroes every exported value (used when the game stops).</summary>
    public void Clear()
    {
        for (int i = 0; i < Wheels.Count; i++)
        {
            Slip[i] = 0;
            Lock[i] = 0;
            Abs[i] = 0;
            Tc[i] = 0;
            SlipBlend[i] = 0;
            LockBlend[i] = 0;
            SlipTc[i] = 0;
            LockAbs[i] = 0;
            BaseSlip[i] = 0;
            Loads[i] = 0;
        }

        SlipMono = 0;
        LockMono = 0;
        AbsMono = 0;
        TcMono = 0;
        SlipBlendMono = 0;
        LockBlendMono = 0;
        SlipTcMono = 0;
        LockAbsMono = 0;
        BaseSlipMono = 0;
        BaseLockMono = 0;
        BaseAbsMono = 0;
        BaseTcMono = 0;
        AggregateUsesTc = false;
        AggregateUsesAbs = false;
    }
}
