using System;
using System.Collections.Generic;
using User.SlipLockPropertiesCalc.Balance;
using User.SlipLockPropertiesCalc.Core;
using User.SlipLockPropertiesCalc.SlipLock;

namespace User.SlipLockPropertiesCalc.Settings;

/// <summary>
/// Global plugin settings, persisted by SimHub (<c>ReadCommonSettings/SaveCommonSettings("GeneralSettings")</c>)
/// to <c>PluginsData\Common\SlipLockPropertiesCalc.GeneralSettings.json</c>.
/// COMPATIBILITY: the v1 field names below must never be renamed; v1 files load unchanged.
/// Plain public fields (JSON format). The data thread owns the object: SimHub runs as a 32-bit process, where an
/// 8-byte double written on one thread can be seen torn by another, so the UI edits doubles through
/// <c>ISlipLockHost.EditSettings</c> (applied on the data thread). bool/int/enum fields are atomic and may be written
/// by the UI directly.
/// Per-car values (sensitivities, learned vehicle model) live in <see cref="CarProfile"/> files instead.
/// </summary>
public sealed class PluginSettings
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Absent (0) in v1 files.</summary>
    public int SchemaVersion = CurrentSchemaVersion;

    // ---- v1 fields (names are part of the file format) ----
    public double SlipThrottleBlend = 20.0;
    public double TCThrottleBlend = 50.0;
    public double LockBrakeBlend = 20.0;
    public double ABSBrakeBlend = 50.0;

    public double SlipThreshold = 5.0;
    public double LockThreshold = 5.0;
    public double TCThreshold = 5.0;
    public double ABSThreshold = 5.0;

    public bool GateSlipOnThrottle;
    public bool GateLockOnBrake;

    public double SlipAttackMs = 10;
    public double SlipReleaseMs = 100;
    public double LockAttackMs = 10;
    public double LockReleaseMs = 100;
    public double ABSAttackMs = 5;
    public double ABSReleaseMs = 50;
    public double TCAttackMs = 5;
    public double TCReleaseMs = 50;

    /// <summary>
    /// Unused leftover of the SimHub SDK demo settings that v1 inherited. Kept only so existing files keep the
    /// field when they are saved again (v1 field names are never removed).
    /// </summary>
    public int SpeedWarningLevel = 100;

    public Dictionary<string, GameCapabilities> GameCapabilities = new Dictionary<string, GameCapabilities>(StringComparer.OrdinalIgnoreCase);

    // ---- v2 fields ----

    /// <summary>Show the full debug/advanced view (hidden by default).</summary>
    public bool ShowDebugView;

    /// <summary>Merge ShakeIT's WheelLock export into the lock channel when available.</summary>
    public bool UseShakeItWheelLock = true;

    /// <summary>Write the 1 Hz diagnostic log to <c>Logs\SlipLock_debug.log</c>.</summary>
    public bool DebugFileLog;

    /// <summary>Global understeer/oversteer detector tuning.</summary>
    public BalanceTuning Balance = new BalanceTuning();

    /// <summary>Per-sim runtime sign calibration (key = SimHub game name).</summary>
    public Dictionary<string, SimCalibration> BalanceCalibration = new Dictionary<string, SimCalibration>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the post-processor settings into the processor's tuning object (sensitivities untouched).</summary>
    internal void CopyTo(SlipLockTuning tuning)
    {
        tuning.SlipThrottleBlend = SlipThrottleBlend;
        tuning.TcThrottleBlend = TCThrottleBlend;
        tuning.LockBrakeBlend = LockBrakeBlend;
        tuning.AbsBrakeBlend = ABSBrakeBlend;
        tuning.SlipThreshold = SlipThreshold;
        tuning.LockThreshold = LockThreshold;
        tuning.TcThreshold = TCThreshold;
        tuning.AbsThreshold = ABSThreshold;
        tuning.GateSlipOnThrottle = GateSlipOnThrottle;
        tuning.GateLockOnBrake = GateLockOnBrake;
        tuning.SlipAttackMs = SlipAttackMs;
        tuning.SlipReleaseMs = SlipReleaseMs;
        tuning.LockAttackMs = LockAttackMs;
        tuning.LockReleaseMs = LockReleaseMs;
        tuning.AbsAttackMs = ABSAttackMs;
        tuning.AbsReleaseMs = ABSReleaseMs;
        tuning.TcAttackMs = TCAttackMs;
        tuning.TcReleaseMs = TCReleaseMs;
        tuning.UseShakeItWheelLock = UseShakeItWheelLock;
    }

    /// <summary>Restores the v1 post-processor slider defaults (debug view "reset").</summary>
    public void ResetSlipLockTuningToDefaults()
    {
        var d = new PluginSettings();
        SlipThrottleBlend = d.SlipThrottleBlend;
        TCThrottleBlend = d.TCThrottleBlend;
        LockBrakeBlend = d.LockBrakeBlend;
        ABSBrakeBlend = d.ABSBrakeBlend;
        SlipThreshold = d.SlipThreshold;
        LockThreshold = d.LockThreshold;
        TCThreshold = d.TCThreshold;
        ABSThreshold = d.ABSThreshold;
        GateSlipOnThrottle = d.GateSlipOnThrottle;
        GateLockOnBrake = d.GateLockOnBrake;
        SlipAttackMs = d.SlipAttackMs;
        SlipReleaseMs = d.SlipReleaseMs;
        LockAttackMs = d.LockAttackMs;
        LockReleaseMs = d.LockReleaseMs;
        ABSAttackMs = d.ABSAttackMs;
        ABSReleaseMs = d.ABSReleaseMs;
        TCAttackMs = d.TCAttackMs;
        TCReleaseMs = d.TCReleaseMs;
        UseShakeItWheelLock = d.UseShakeItWheelLock;
    }

    /// <summary>
    /// Repairs a freshly deserialized instance: null collections, case-insensitive keys, out-of-range values.
    /// Call once after <c>ReadCommonSettings</c>.
    /// </summary>
    public void Normalize()
    {
        GameCapabilities = CopyCaseInsensitive(GameCapabilities);
        foreach (string key in new List<string>(GameCapabilities.Keys))
        {
            if (GameCapabilities[key] == null)
            {
                GameCapabilities[key] = new GameCapabilities();
            }
        }

        BalanceCalibration = CopyCaseInsensitive(BalanceCalibration);
        foreach (string key in new List<string>(BalanceCalibration.Keys))
        {
            SimCalibration calibration = BalanceCalibration[key] ?? new SimCalibration();
            calibration.Normalize();
            BalanceCalibration[key] = calibration;
        }

        Balance ??= new BalanceTuning();
        Balance.Normalize();

        SlipThrottleBlend = Percent(SlipThrottleBlend, 20.0);
        TCThrottleBlend = Percent(TCThrottleBlend, 50.0);
        LockBrakeBlend = Percent(LockBrakeBlend, 20.0);
        ABSBrakeBlend = Percent(ABSBrakeBlend, 50.0);
        SlipThreshold = Range(SlipThreshold, 5.0, 0, 50);
        LockThreshold = Range(LockThreshold, 5.0, 0, 50);
        TCThreshold = Range(TCThreshold, 5.0, 0, 50);
        ABSThreshold = Range(ABSThreshold, 5.0, 0, 50);
        SlipAttackMs = Range(SlipAttackMs, 10, 0, 1000);
        SlipReleaseMs = Range(SlipReleaseMs, 100, 0, 2000);
        LockAttackMs = Range(LockAttackMs, 10, 0, 1000);
        LockReleaseMs = Range(LockReleaseMs, 100, 0, 2000);
        ABSAttackMs = Range(ABSAttackMs, 5, 0, 1000);
        ABSReleaseMs = Range(ABSReleaseMs, 50, 0, 2000);
        TCAttackMs = Range(TCAttackMs, 5, 0, 1000);
        TCReleaseMs = Range(TCReleaseMs, 50, 0, 2000);
        SchemaVersion = CurrentSchemaVersion;
    }

    /// <summary>Returns (creating if needed) the calibration entry for a sim.</summary>
    public SimCalibration GetCalibration(string simKey)
    {
        string key = simKey ?? string.Empty;
        if (!BalanceCalibration.TryGetValue(key, out SimCalibration calibration) || calibration == null)
        {
            calibration = new SimCalibration();
            BalanceCalibration[key] = calibration;
        }

        return calibration;
    }

    private static Dictionary<string, T> CopyCaseInsensitive<T>(Dictionary<string, T> source)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (source != null)
        {
            foreach (KeyValuePair<string, T> entry in source)
            {
                if (entry.Key != null)
                {
                    result[entry.Key] = entry.Value;
                }
            }
        }

        return result;
    }

    private static double Percent(double value, double fallback) => Range(value, fallback, 0, 100);

    private static double Range(double value, double fallback, double min, double max) =>
        MathUtil.IsFinite(value) ? MathUtil.Clamp(value, min, max) : fallback;
}
