using System;
using System.Collections.Generic;
using User.SlipLockPropertiesCalc.Settings;
using User.SlipLockPropertiesCalc.SlipLock;

namespace User.SlipLockPropertiesCalc.Tests.Legacy;

/// <summary>
/// Equivalence oracle: a verbatim port of the v1 slip/lock math (legacy <c>SlipLockPropertiesCalc.DataUpdate</c>
/// lines 374-571 plus <c>UpdMax</c>, <c>CalcL</c>, <c>Norm</c>, <c>Thresh</c>, <c>R</c>, <c>ApplyEnvelope</c>).
/// Deliberately NOT refactored: same statements, same operation order, same per-frame allocations, v1's lock rule
/// (synthesized from slip for every source when the preset says so) and v1's dt handling. Only the SimHub reads were
/// replaced by the <see cref="SlipLockInputs"/> fields and <c>pm.SetPropertyValue</c> by <see cref="Exports"/>.
/// DateTime.Now was replaced by the frame's wall time (seconds) so runs are deterministic.
/// Do not "fix" anything here: its only purpose is to reproduce v1 outputs.
/// </summary>
internal sealed class LegacyPipeline
{
    private static readonly string[] W = { "FrontLeft", "FrontRight", "RearLeft", "RearRight" };
    private static readonly int[] LatSign = { -1, 1, -1, 1 };
    private static readonly int[] LongSign = { 1, 1, -1, -1 };

    // v1 wrote "SlipLock.<Channel>.<Wheel>" with string interpolation every frame; the names are precomputed here
    // (identical strings) only to keep the randomized tests fast.
    private static readonly string[] SlipNames = Names("SlipLock.Slip.");
    private static readonly string[] LockNames = Names("SlipLock.Lock.");
    private static readonly string[] AbsNames = Names("SlipLock.ABS.");
    private static readonly string[] TcNames = Names("SlipLock.TC.");
    private static readonly string[] SlipBlendNames = Names("SlipLock.SlipBlend.");
    private static readonly string[] LockBlendNames = Names("SlipLock.LockBlend.");
    private static readonly string[] SlipTcNames = Names("SlipLock.SlipTC.");
    private static readonly string[] LockAbsNames = Names("SlipLock.LockABS.");

    private readonly GamePreset _preset;

    // Settings copies (v1 kept private fields in sync with the settings object).
    private readonly double _slipThrottleBlend, _tcThrottleBlend, _lockBrakeBlend, _absBrakeBlend;
    private readonly double _slipThreshold, _lockThreshold, _tcThreshold, _absThreshold;
    private readonly bool _gateSlipOnThrottle, _gateLockOnBrake;
    private readonly double _slipAttackMs, _slipReleaseMs, _lockAttackMs, _lockReleaseMs;
    private readonly double _absAttackMs, _absReleaseMs, _tcAttackMs, _tcReleaseMs;

    // Envelope runtime state (not persisted)
    private double[] _envSlip = new double[4], _envLock = new double[4], _envABS = new double[4], _envTC = new double[4];
    private double _lastFrameTime = double.MinValue; // v1: DateTime.MinValue

    public LegacyPipeline(PluginSettings settings, GamePreset preset)
    {
        _preset = preset;
        _slipThrottleBlend = settings.SlipThrottleBlend; _tcThrottleBlend = settings.TCThrottleBlend;
        _lockBrakeBlend = settings.LockBrakeBlend; _absBrakeBlend = settings.ABSBrakeBlend;
        _slipThreshold = settings.SlipThreshold; _lockThreshold = settings.LockThreshold;
        _tcThreshold = settings.TCThreshold; _absThreshold = settings.ABSThreshold;
        _gateSlipOnThrottle = settings.GateSlipOnThrottle; _gateLockOnBrake = settings.GateLockOnBrake;
        _slipAttackMs = settings.SlipAttackMs; _slipReleaseMs = settings.SlipReleaseMs;
        _lockAttackMs = settings.LockAttackMs; _lockReleaseMs = settings.LockReleaseMs;
        _absAttackMs = settings.ABSAttackMs; _absReleaseMs = settings.ABSReleaseMs;
        _tcAttackMs = settings.TCAttackMs; _tcReleaseMs = settings.TCReleaseMs;
    }

    /// <summary>Every value v1 wrote with <c>pm.SetPropertyValue</c> (43 properties), keyed by v1 property name.</summary>
    public Dictionary<string, double> Exports { get; } = new Dictionary<string, double>(StringComparer.Ordinal);

    // ---- v1 UI display values (debug) ----
    public double MaxSway = 5, MaxSurge = 5, MaxDecel = 5;
    public double BaseSlipMono, BaseLockMono, BaseABSMono, BaseTCMono;
    public double BaseSlipFL, BaseSlipFR, BaseSlipRL, BaseSlipRR;
    public double LoadFL, LoadFR, LoadRL, LoadRR;
    public string SlipTCMode = "...", LockABSMode = "...";

    /// <summary>v1 car change: max values back to 5.</summary>
    public void ResetMax() { MaxSway = 5; MaxSurge = 5; MaxDecel = 5; }

    /// <summary>One v1 DataUpdate (from the telemetry reads onwards). <paramref name="rawSlip"/> = v1 GetBaseSlip result.</summary>
    public void Frame(SlipLockInputs input, double[] rawSlip)
    {
        double throttle = input.Throttle, brake = input.Brake;
        double sway = input.Sway, surge = input.Surge;
        bool absActive = input.AbsActive, tcActive = input.TcActive;

        double tcLevel = input.TcLevel, absLevel = input.AbsLevel; bool gExpTC = input.GameExportsTc, gExpABS = input.GameExportsAbs;

        UpdMax(sway, surge);

        // Fixed aggregate decision
        bool aggUsesTC = gExpTC && tcLevel > 0;
        bool aggUsesABS = gExpABS && absLevel > 0;

        double tN = throttle / 100, bN = brake / 100;

        // Base display
        BaseSlipMono = rawSlip[0]; BaseLockMono = _preset.SynthLockFromSlip && brake > 5 && surge > 0.1 ? rawSlip[0] : 0;
        BaseABSMono = absActive ? 1.0 : 0; BaseTCMono = tcActive ? 1.0 : 0;
        BaseSlipFL = rawSlip[0]; BaseSlipFR = rawSlip[1]; BaseSlipRL = rawSlip[2]; BaseSlipRR = rawSlip[3];

        // Compute proxyL loads for each channel type
        double[] slipL = CalcL(sway, surge, _preset.SlipLat / 100, _preset.SlipLong / 100);
        double[] lockL = CalcL(sway, surge, _preset.LockLat / 100, _preset.LockLong / 100);
        double[] absL = CalcL(sway, surge, _preset.AbsLat / 100, _preset.AbsLong / 100);
        double[] tcL = CalcL(sway, surge, _preset.TcLat / 100, _preset.TcLong / 100);

        // Display slip loads
        LoadFL = slipL[0]; LoadFR = slipL[1]; LoadRL = slipL[2]; LoadRR = slipL[3];

        // Speed fade: linearly reduce slip/lock below preset speed threshold
        double speedFade = 1.0;
        if (_preset.SpeedFadeKmh > 0)
        {
            double speedKmh = input.SpeedKmh;
            speedFade = Math.Min(1.0, Math.Max(0, speedKmh / _preset.SpeedFadeKmh));
        }

        // Apply proxyL and normalize
        double[] slip = new double[4], lockV = new double[4], absV = new double[4], tcV = new double[4];
        for (int i = 0; i < 4; i++)
        {
            double slipRaw = Math.Max(0, rawSlip[i]);
            if (_preset.InverseSlipLoad)
                slip[i] = Norm(slipRaw * 25.0 / Math.Max(1, slipL[i]) * speedFade);
            else
                slip[i] = Norm(slipRaw * slipL[i] / 25.0 * speedFade);

            if (_preset.SynthLockFromSlip)
                lockV[i] = (brake > 5 && surge > 0.1) ? Norm(Math.Max(0, rawSlip[i]) * lockL[i] / 25.0 * speedFade) : 0;
            else
                lockV[i] = Norm(Math.Abs(Math.Min(0, rawSlip[i])) * lockL[i] / 25.0 * speedFade);

            absV[i] = absActive ? Norm(100.0 * absL[i] / 25.0) : 0;
            tcV[i] = tcActive ? Norm(100.0 * tcL[i] / 25.0) : 0;
        }

        // Preprocessor gain + cut (applied to slip and lock only, ABS/TC are binary-derived)
        if (_preset.PreCut > 0 || _preset.PreGain < 100)
        {
            double gain = _preset.PreGain / 100.0;
            double cut = _preset.PreCut;
            for (int i = 0; i < 4; i++)
            {
                slip[i] = slip[i] * gain;
                slip[i] = slip[i] <= cut ? 0 : (slip[i] - cut) / (100.0 - cut) * 100.0;
                slip[i] = Norm(slip[i]);

                lockV[i] = lockV[i] * gain;
                lockV[i] = lockV[i] <= cut ? 0 : (lockV[i] - cut) / (100.0 - cut) * 100.0;
                lockV[i] = Norm(lockV[i]);
            }
        }

        for (int i = 0; i < 4; i++)
        {
            Set(SlipNames[i], R(slip[i]));
            Set(LockNames[i], R(lockV[i]));
            Set(AbsNames[i], R(absV[i]));
            Set(TcNames[i], R(tcV[i]));
        }
        Set("SlipLock.Slip.Mono", R((slip[0] + slip[1] + slip[2] + slip[3]) / 4));
        Set("SlipLock.Lock.Mono", R((lockV[0] + lockV[1] + lockV[2] + lockV[3]) / 4));
        Set("SlipLock.ABS.Mono", R((absV[0] + absV[1] + absV[2] + absV[3]) / 4));
        Set("SlipLock.TC.Mono", R((tcV[0] + tcV[1] + tcV[2] + tcV[3]) / 4));

        // ===== POSTPROCESSOR =====

        // Thresholds
        for (int i = 0; i < 4; i++)
        {
            slip[i] = Thresh(slip[i], _slipThreshold);
            lockV[i] = Thresh(lockV[i], _lockThreshold);
            absV[i] = Thresh(absV[i], _absThreshold);
            tcV[i] = Thresh(tcV[i], _tcThreshold);
        }

        // Gates
        bool slipGated = _gateSlipOnThrottle && throttle <= 0;
        bool lockGated = _gateLockOnBrake && brake <= 0;
        if (slipGated) for (int i = 0; i < 4; i++) { slip[i] = 0; tcV[i] = 0; }
        if (lockGated) for (int i = 0; i < 4; i++) { lockV[i] = 0; absV[i] = 0; }

        // Envelope shaping
        var now = input.WallTime;
        double dt = _lastFrameTime == double.MinValue ? 0.016 : (now - _lastFrameTime);
        _lastFrameTime = now;
        dt = Math.Max(0.001, Math.Min(0.1, dt));

        for (int i = 0; i < 4; i++)
        {
            slip[i] = ApplyEnvelope(ref _envSlip[i], slip[i], dt, _slipAttackMs, _slipReleaseMs);
            lockV[i] = ApplyEnvelope(ref _envLock[i], lockV[i], dt, _lockAttackMs, _lockReleaseMs);
            absV[i] = ApplyEnvelope(ref _envABS[i], absV[i], dt, _absAttackMs, _absReleaseMs);
            tcV[i] = ApplyEnvelope(ref _envTC[i], tcV[i], dt, _tcAttackMs, _tcReleaseMs);
        }

        // 4 separate pedal blends
        double stB = _slipThrottleBlend / 100, tcB = _tcThrottleBlend / 100;
        double lbB = _lockBrakeBlend / 100, abB = _absBrakeBlend / 100;
        double[] slipB = new double[4], lockB = new double[4];
        for (int i = 0; i < 4; i++)
        {
            slipB[i] = R(slip[i] * (1 - stB) + slip[i] * tN * stB);
            lockB[i] = R(lockV[i] * (1 - lbB) + lockV[i] * bN * lbB);
            Set(SlipBlendNames[i], slipB[i]);
            Set(LockBlendNames[i], lockB[i]);
        }
        Set("SlipLock.SlipBlend.Mono", R((slipB[0] + slipB[1] + slipB[2] + slipB[3]) / 4));
        Set("SlipLock.LockBlend.Mono", R((lockB[0] + lockB[1] + lockB[2] + lockB[3]) / 4));

        // Fixed aggregates
        double[] aggS = new double[4], aggL = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (aggUsesTC)
                aggS[i] = R(tcV[i] * (1 - tcB) + tcV[i] * tN * tcB);
            else
                aggS[i] = slipB[i];

            if (aggUsesABS)
                aggL[i] = R(absV[i] * (1 - abB) + absV[i] * bN * abB);
            else
                aggL[i] = lockB[i];

            Set(SlipTcNames[i], aggS[i]);
            Set(LockAbsNames[i], aggL[i]);
        }
        Set("SlipLock.SlipTC.Mono", R((aggS[0] + aggS[1] + aggS[2] + aggS[3]) / 4));
        Set("SlipLock.LockABS.Mono", R((aggL[0] + aggL[1] + aggL[2] + aggL[3]) / 4));

        // Fixed mode labels
        SlipTCMode = aggUsesTC ? "TC" : "Slip";
        LockABSMode = aggUsesABS ? "ABS" : "Lock";

        Set("SlipLock.MaxSway", MaxSway);
        Set("SlipLock.MaxSurge", MaxSurge);
        Set("SlipLock.MaxDecel", MaxDecel);
    }

    private void Set(string name, double value) => Exports[name] = value;

    // ==================== ProxyL ====================
    private double[] CalcL(double sway, double surge, double latI, double longI)
    {
        double nL = Math.Min(100, Math.Abs(sway) / Math.Max(MaxSway, 0.1) * 100) * latI;
        double nG = Math.Min(100, Math.Abs(surge) / Math.Max(MaxSurge, 0.1) * 100) * longI;
        var r = new double[4];
        for (int i = 0; i < 4; i++)
        {
            double la = (sway < 0) == (LatSign[i] < 0) ? nL : -nL;
            double lo = (surge > 0) == (LongSign[i] > 0) ? nG : -nG;
            r[i] = 25 + 25 * la / 100; r[i] += r[i] * lo / 100;
            r[i] = Math.Max(0, Math.Min(50, r[i]));
        }
        return r;
    }

    private void UpdMax(double sw, double su) { double s = Math.Abs(sw); if (s > MaxSway && s < MaxSway + 5) MaxSway = s; if (su > MaxSurge && su < MaxSurge + 5) MaxSurge = su; double d = -su; if (d > MaxDecel && d < MaxDecel + 5) MaxDecel = d; }

    private static double Norm(double v) => Math.Max(0, Math.Min(100, v));
    private static double Thresh(double v, double t) => v <= t ? 0 : (v - t) / (100 - t) * 100;
    private static double R(double v) => Math.Round(v, 1);

    private static double ApplyEnvelope(ref double state, double target, double dt, double attackMs, double releaseMs)
    {
        if (target >= state)
        {
            if (attackMs < 1) { state = target; }
            else
            {
                double rate = 1.0 - Math.Exp(-dt / (attackMs / 1000.0));
                state += (target - state) * rate;
            }
        }
        else
        {
            double level = Math.Max(0.01, state / 100.0);
            double effectiveMs = releaseMs * Math.Sqrt(level);
            if (effectiveMs < 1) { state = target; }
            else
            {
                double rate = 1.0 - Math.Exp(-dt / (effectiveMs / 1000.0));
                state += (target - state) * rate;
            }
        }
        if (state < 0.001) state = 0;
        return Math.Max(0, Math.Min(100, state));
    }

    private static string[] Names(string prefix)
    {
        var names = new string[4];
        for (int i = 0; i < 4; i++)
        {
            names[i] = prefix + W[i];
        }

        return names;
    }
}
