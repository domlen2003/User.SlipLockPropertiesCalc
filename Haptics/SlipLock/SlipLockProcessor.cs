using System;
using DivebombLogistics.Core;

namespace DivebombLogistics.Haptics.SlipLock;

/// <summary>
/// The slip/lock/ABS/TC pipeline of v1 (pre-processor: corner load, speed fade, pre-gain/cut; post-processor:
/// thresholds, pedal gates, envelopes, pedal blends, TC/ABS aggregates), plus three deliberate v2 changes:
/// <list type="bullet">
/// <item><description>A: per-car slip/lock sensitivity factors (1.0 reproduces v1 bit for bit).</description></item>
/// <item><description>B: signed sources (rFactor rotation, per-wheel speed) always take lock from negative slip;
/// v1 synthesized lock from positive slip for every source, which inverted lock for signed sources.</description></item>
/// <item><description>C: ShakeIT's <c>WheelLock</c> export is merged into the lock channel (max of both); v1 lock was
/// synthesized from ShakeIT slip only, which reads 0 in heavy braking for cars like the LMU LMP3.</description></item>
/// </list>
/// With sensitivities 1.0, an unsigned source and no ShakeIT lock, every output equals v1 exactly (verified by
/// <c>Tests/SlipLockProcessorTests</c> against a verbatim port of the v1 code) — keep the operation order when editing.
/// Pure and allocation-free; one instance per plugin, called from the data thread only.
/// </summary>
internal sealed class SlipLockProcessor
{
    /// <summary>Envelope dt used for the very first frame (v1: one 60 Hz frame).</summary>
    private const double FirstFrameDt = 0.016;

    /// <summary>Envelope dt bounds (v1) so pauses and stutters do not make the envelopes jump.</summary>
    private const double MinDt = 0.001;

    private const double MaxDt = 0.1;

    /// <summary>Channel values are percent (0..100).</summary>
    private const double FullScale = 100.0;

    /// <summary>ABS/TC are on/off flags; while active they drive their channel at full scale before load weighting.</summary>
    private const double FlagActiveLevel = 100.0;

    /// <summary>Synthesized lock requires brake above this (percent).</summary>
    private const double SynthLockMinBrake = 5.0;

    /// <summary>Synthesized lock requires deceleration above this (SimHub surge is positive under braking).</summary>
    private const double SynthLockMinSurge = 0.1;

    /// <summary>Lower bound of the corner load when slip is divided by it (inverse load presets).</summary>
    private const double MinInverseLoad = 1.0;

    private readonly double[] slipLoads = new double[Wheels.Count];
    private readonly double[] lockLoads = new double[Wheels.Count];
    private readonly double[] absLoads = new double[Wheels.Count];
    private readonly double[] tcLoads = new double[Wheels.Count];

    private readonly double[] slip = new double[Wheels.Count];
    private readonly double[] lockValues = new double[Wheels.Count];
    private readonly double[] abs = new double[Wheels.Count];
    private readonly double[] tc = new double[Wheels.Count];

    private readonly double[] slipEnvelope = new double[Wheels.Count];
    private readonly double[] lockEnvelope = new double[Wheels.Count];
    private readonly double[] absEnvelope = new double[Wheels.Count];
    private readonly double[] tcEnvelope = new double[Wheels.Count];

    private bool hasLastWallTime;
    private double lastWallTime;

    /// <summary>True when the last frame synthesized lock from positive slip (unsigned source, preset allows it).</summary>
    public bool LockSynthesizedFromSlip { get; private set; }

    /// <summary>True when the last frame merged ShakeIT's WheelLock export into the lock channel.</summary>
    public bool LockMergedShakeIt { get; private set; }

    /// <summary>Clears the envelopes and the frame clock (next frame uses the first-frame dt).</summary>
    public void Reset()
    {
        Array.Clear(slipEnvelope, 0, Wheels.Count);
        Array.Clear(lockEnvelope, 0, Wheels.Count);
        Array.Clear(absEnvelope, 0, Wheels.Count);
        Array.Clear(tcEnvelope, 0, Wheels.Count);
        hasLastWallTime = false;
        lastWallTime = 0;
        LockSynthesizedFromSlip = false;
        LockMergedShakeIt = false;
    }

    /// <summary>
    /// Runs one frame of the pipeline and writes every exported (and debug) value into <paramref name="output"/>.
    /// Call <see cref="MaxGTracker.Update"/> for this frame first (v1 order).
    /// </summary>
    public void Process(SlipLockInputs input, GamePreset preset, SlipLockTuning tuning, MaxGTracker maxG, SlipLockOutputs output)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (preset == null)
        {
            throw new ArgumentNullException(nameof(preset));
        }

        if (tuning == null)
        {
            throw new ArgumentNullException(nameof(tuning));
        }

        if (maxG == null)
        {
            throw new ArgumentNullException(nameof(maxG));
        }

        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        // Non-finite telemetry would otherwise poison the envelope states permanently; finite values pass unchanged.
        double throttle = FiniteOrZero(input.Throttle);
        double brake = FiniteOrZero(input.Brake);
        double sway = FiniteOrZero(input.Sway);
        double surge = FiniteOrZero(input.Surge);
        double speedKmh = FiniteOrZero(input.SpeedKmh);
        double slipSensitivity = SanitizeSensitivity(tuning.SlipSensitivity);
        double lockSensitivity = SanitizeSensitivity(tuning.LockSensitivity);

        bool braking = brake > SynthLockMinBrake && surge > SynthLockMinSurge;
        bool synthesizeLock = preset.SynthLockFromSlip && !input.BaseSlipIsSigned;
        bool mergeShakeItLock = tuning.UseShakeItWheelLock && input.HasShakeItLock;
        LockSynthesizedFromSlip = synthesizeLock;
        LockMergedShakeIt = mergeShakeItLock;

        output.AggregateUsesTc = input.GameExportsTc && input.TcLevel > 0;
        output.AggregateUsesAbs = input.GameExportsAbs && input.AbsLevel > 0;

        WriteBaseDebug(input, synthesizeLock, braking, output);

        ProxyLoad.Calc(sway, surge, preset.SlipLat / 100, preset.SlipLong / 100, maxG.MaxSway, maxG.MaxSurge, slipLoads);
        ProxyLoad.Calc(sway, surge, preset.LockLat / 100, preset.LockLong / 100, maxG.MaxSway, maxG.MaxSurge, lockLoads);
        ProxyLoad.Calc(sway, surge, preset.AbsLat / 100, preset.AbsLong / 100, maxG.MaxSway, maxG.MaxSurge, absLoads);
        ProxyLoad.Calc(sway, surge, preset.TcLat / 100, preset.TcLong / 100, maxG.MaxSway, maxG.MaxSurge, tcLoads);
        Array.Copy(slipLoads, output.Loads, Wheels.Count);

        double speedFade = preset.SpeedFadeKmh > 0 ? Math.Min(1.0, Math.Max(0, speedKmh / preset.SpeedFadeKmh)) : 1.0;

        for (int i = 0; i < Wheels.Count; i++)
        {
            double raw = BaseSlipAt(input, i);

            // Sensitivity multiplies the raw signal first so that a factor of 1.0 keeps v1's exact operation order.
            double slipRaw = Math.Max(0, raw) * slipSensitivity;
            slip[i] = preset.InverseSlipLoad
                ? Norm(slipRaw * ProxyLoad.Neutral / Math.Max(MinInverseLoad, slipLoads[i]) * speedFade) // rotation slip already grows with load
                : Norm(slipRaw * slipLoads[i] / ProxyLoad.Neutral * speedFade);

            double lockRaw;
            if (synthesizeLock)
            {
                // Unsigned sources cannot tell spin from lock: positive slip while braking counts as lock.
                lockRaw = braking ? Math.Max(0, raw) : 0;
            }
            else
            {
                // Signed sources (and presets without synthesis): negative slip, i.e. wheel slower than the car, is lock.
                lockRaw = Math.Abs(Math.Min(0, raw));
            }

            lockValues[i] = Norm(lockRaw * lockSensitivity * lockLoads[i] / ProxyLoad.Neutral * speedFade);

            if (mergeShakeItLock)
            {
                double shakeItLock = Math.Max(0, FiniteOrZero(input.ShakeItLock[i]));
                lockValues[i] = Math.Max(lockValues[i], Norm(shakeItLock * lockSensitivity * lockLoads[i] / ProxyLoad.Neutral * speedFade));
            }

            abs[i] = input.AbsActive ? Norm(FlagActiveLevel * absLoads[i] / ProxyLoad.Neutral) : 0;
            tc[i] = input.TcActive ? Norm(FlagActiveLevel * tcLoads[i] / ProxyLoad.Neutral) : 0;
        }

        ApplyPreGainAndCut(preset);
        WritePreprocessorOutputs(output);

        ApplyThresholds(tuning);
        ApplyGates(tuning, throttle, brake);
        ApplyEnvelopes(tuning, NextDt(input.WallTime));

        WriteBlendsAndAggregates(tuning, throttle / 100, brake / 100, output);
    }

    /// <summary>Unrounded debug copy of the raw base signal (v1 "base" display rows).</summary>
    private static void WriteBaseDebug(SlipLockInputs input, bool synthesizeLock, bool braking, SlipLockOutputs output)
    {
        double rawFrontLeft = BaseSlipAt(input, Wheels.FrontLeft);
        output.BaseSlipMono = rawFrontLeft;

        // Shows the signal the lock channel is derived from (v1 only knew the synthesized variant).
        if (synthesizeLock)
        {
            output.BaseLockMono = braking ? rawFrontLeft : 0;
        }
        else
        {
            output.BaseLockMono = input.BaseSlipIsSigned ? Math.Abs(Math.Min(0, rawFrontLeft)) : 0;
        }

        output.BaseAbsMono = input.AbsActive ? 1.0 : 0;
        output.BaseTcMono = input.TcActive ? 1.0 : 0;
        for (int i = 0; i < Wheels.Count; i++)
        {
            output.BaseSlip[i] = BaseSlipAt(input, i);
        }
    }

    /// <summary>Preset gain and noise cut on slip and lock (ABS/TC are flag-derived and skip it).</summary>
    private void ApplyPreGainAndCut(GamePreset preset)
    {
        if (!(preset.PreCut > 0 || preset.PreGain < 100))
        {
            return;
        }

        double gain = preset.PreGain / 100.0;
        double cut = preset.PreCut;
        for (int i = 0; i < Wheels.Count; i++)
        {
            slip[i] = GainAndCut(slip[i], gain, cut);
            lockValues[i] = GainAndCut(lockValues[i], gain, cut);
        }
    }

    private void WritePreprocessorOutputs(SlipLockOutputs output)
    {
        for (int i = 0; i < Wheels.Count; i++)
        {
            output.Slip[i] = R(slip[i]);
            output.Lock[i] = R(lockValues[i]);
            output.Abs[i] = R(abs[i]);
            output.Tc[i] = R(tc[i]);
        }

        // v1 averages the unrounded values, then rounds.
        output.SlipMono = R(MathUtil.Average4(slip));
        output.LockMono = R(MathUtil.Average4(lockValues));
        output.AbsMono = R(MathUtil.Average4(abs));
        output.TcMono = R(MathUtil.Average4(tc));
    }

    private void ApplyThresholds(SlipLockTuning tuning)
    {
        for (int i = 0; i < Wheels.Count; i++)
        {
            slip[i] = Thresh(slip[i], tuning.SlipThreshold);
            lockValues[i] = Thresh(lockValues[i], tuning.LockThreshold);
            abs[i] = Thresh(abs[i], tuning.AbsThreshold);
            tc[i] = Thresh(tc[i], tuning.TcThreshold);
        }
    }

    /// <summary>Optional pedal gates: no slip/TC without throttle, no lock/ABS without brake.</summary>
    private void ApplyGates(SlipLockTuning tuning, double throttle, double brake)
    {
        if (tuning.GateSlipOnThrottle && throttle <= 0)
        {
            Array.Clear(slip, 0, Wheels.Count);
            Array.Clear(tc, 0, Wheels.Count);
        }

        if (tuning.GateLockOnBrake && brake <= 0)
        {
            Array.Clear(lockValues, 0, Wheels.Count);
            Array.Clear(abs, 0, Wheels.Count);
        }
    }

    private void ApplyEnvelopes(SlipLockTuning tuning, double dt)
    {
        for (int i = 0; i < Wheels.Count; i++)
        {
            slip[i] = LegacyEnvelope.Apply(ref slipEnvelope[i], slip[i], dt, tuning.SlipAttackMs, tuning.SlipReleaseMs);
            lockValues[i] = LegacyEnvelope.Apply(ref lockEnvelope[i], lockValues[i], dt, tuning.LockAttackMs, tuning.LockReleaseMs);
            abs[i] = LegacyEnvelope.Apply(ref absEnvelope[i], abs[i], dt, tuning.AbsAttackMs, tuning.AbsReleaseMs);
            tc[i] = LegacyEnvelope.Apply(ref tcEnvelope[i], tc[i], dt, tuning.TcAttackMs, tuning.TcReleaseMs);
        }
    }

    /// <summary>
    /// Pedal blends (share of the signal modulated by the pedal) and the TC/ABS aggregates.
    /// Monos here average the rounded per-wheel values (v1).
    /// </summary>
    private void WriteBlendsAndAggregates(SlipLockTuning tuning, double throttleFactor, double brakeFactor, SlipLockOutputs output)
    {
        double slipBlend = tuning.SlipThrottleBlend / 100;
        double tcBlend = tuning.TcThrottleBlend / 100;
        double lockBlend = tuning.LockBrakeBlend / 100;
        double absBlend = tuning.AbsBrakeBlend / 100;

        for (int i = 0; i < Wheels.Count; i++)
        {
            output.SlipBlend[i] = R(PedalBlend(slip[i], throttleFactor, slipBlend));
            output.LockBlend[i] = R(PedalBlend(lockValues[i], brakeFactor, lockBlend));
        }

        output.SlipBlendMono = R(MathUtil.Average4(output.SlipBlend));
        output.LockBlendMono = R(MathUtil.Average4(output.LockBlend));

        for (int i = 0; i < Wheels.Count; i++)
        {
            output.SlipTc[i] = output.AggregateUsesTc ? R(PedalBlend(tc[i], throttleFactor, tcBlend)) : output.SlipBlend[i];
            output.LockAbs[i] = output.AggregateUsesAbs ? R(PedalBlend(abs[i], brakeFactor, absBlend)) : output.LockBlend[i];
        }

        output.SlipTcMono = R(MathUtil.Average4(output.SlipTc));
        output.LockAbsMono = R(MathUtil.Average4(output.LockAbs));
    }

    /// <summary>Envelope time step from the wall clock (v1 semantics: first frame 16 ms, clamped to 1..100 ms).</summary>
    private double NextDt(double wallTime)
    {
        if (!MathUtil.IsFinite(wallTime))
        {
            return FirstFrameDt;
        }

        double dt = hasLastWallTime ? wallTime - lastWallTime : FirstFrameDt;
        lastWallTime = wallTime;
        hasLastWallTime = true;
        return Math.Max(MinDt, Math.Min(MaxDt, dt));
    }

    private static double BaseSlipAt(SlipLockInputs input, int wheel) => FiniteOrZero(input.BaseSlip[wheel]);

    /// <summary>(1 - blend) of the signal passes unchanged, the blend share is scaled by the pedal (v1 formula).</summary>
    private static double PedalBlend(double value, double pedalFactor, double blend) => value * (1 - blend) + value * pedalFactor * blend;

    private static double GainAndCut(double value, double gain, double cut)
    {
        value *= gain;
        value = value <= cut ? 0 : (value - cut) / (100.0 - cut) * 100.0;
        return Norm(value);
    }

    /// <summary>Clamps to the 0..100 channel range.</summary>
    private static double Norm(double value) => Math.Max(0, Math.Min(FullScale, value));

    /// <summary>Values at or below the threshold become 0; the rest is remapped to 0..100.</summary>
    private static double Thresh(double value, double threshold) => value <= threshold ? 0 : (value - threshold) / (100 - threshold) * 100;

    /// <summary>Exported values are rounded to 1 decimal (v1).</summary>
    private static double R(double value) => Math.Round(value, 1);

    private static double FiniteOrZero(double value) => MathUtil.FiniteOr(value, 0.0);

    /// <summary>Sensitivity factors come from clamped profile values; anything unusable falls back to 1.0 (v1).</summary>
    private static double SanitizeSensitivity(double factor) => MathUtil.IsFinite(factor) && factor >= 0 ? factor : 1.0;
}
