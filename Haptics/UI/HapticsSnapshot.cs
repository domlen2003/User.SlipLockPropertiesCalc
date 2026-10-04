using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.SlipLock;
using DivebombLogistics.Haptics.Telemetry;

namespace DivebombLogistics.Haptics.UI;

/// <summary>
/// Plain copy of everything the settings UI displays. The data thread fills one instance (under a lock, at a
/// throttled rate); the UI copies it via <see cref="IHapticsHost.CopySnapshot"/> into its own instance at ~10 Hz.
/// Strings are assigned only when they change (no per-frame allocation). Arrays are fixed length 4 (FL, FR, RL, RR).
/// </summary>
public sealed class HapticsSnapshot
{
    private const int W = 4;

    // ---- General ----
    public bool GameRunning;
    public string GameName = string.Empty;
    public bool HasCar;
    public string CarId = string.Empty;
    public string CarKey = string.Empty;
    public string CarDisplayName = string.Empty;
    public string CarClass = string.Empty;

    /// <summary>Increments whenever the current car profile instance changes (car change, import, reset).</summary>
    public int CarProfileVersion;

    public string CarProfilePath = string.Empty;

    /// <summary>Where the car key came from ("native model", "car model", "car id").</summary>
    public string CarKeySource = string.Empty;

    /// <summary>
    /// The key is a livery-specific placeholder while the sim's native model name is expected (LMU's first
    /// seconds): the profile may still switch, so per-car edits are held back.
    /// </summary>
    public bool CarKeyProvisional;

    // ---- Per-car sensitivities (percent) ----
    public double SlipSensitivity = 100;
    public double LockSensitivity = 100;
    public double UndersteerSensitivity = 100;
    public double OversteerSensitivity = 100;

    // ---- Slip source / detection ----
    /// <summary>Source of the base slip in use (per-wheel speed while in per-wheel mode).</summary>
    public SlipSourceKind SlipSource;

    /// <summary>Slip source found by the resolver (ShakeIT, ACC native, rF2 rotation), also in per-wheel mode.</summary>
    public SlipSourceKind ResolvedSlipSource;

    /// <summary>The base slip currently has working data (slip source or per-wheel speeds).</summary>
    public bool SlipDataAvailable;

    /// <summary>ShakeIT's WheelSlip export is the resolved slip source and readable.</summary>
    public bool ShakeItSlipAvailable;
    public bool ShakeItLockAvailable;

    /// <summary>True when the lock channel currently merges ShakeIT WheelLock.</summary>
    public bool LockUsesShakeIt;

    /// <summary>True when lock is synthesized from positive slip while braking (unsigned source), false when measured.</summary>
    public bool LockSynthesized;

    public DetectionState Detection;
    public double DetectSpeed;
    public double DetectLat;
    public double DetectBrake;
    public bool DetectSpeedOk;
    public bool DetectCornerOk;
    public bool DetectBrakeOk;
    public int DetectFrames;
    public double DetectMaxDelta;
    public readonly string[] SlipPaths = new string[W];
    public readonly string[] LockPaths = new string[W];
    public string PresetName = string.Empty;
    public GamePreset Preset;

    // ---- Capabilities ----
    public bool GameExportsAbs;
    public bool GameExportsTc;
    public TriState CarHasAbs;
    public TriState CarHasTc;
    public double AbsLevel = -1;
    public double TcLevel = -1;
    public bool AggregateUsesTc;
    public bool AggregateUsesAbs;
    public double MaxSway = 5;
    public double MaxSurge = 5;
    public double MaxDecel = 5;

    // ---- Slip/lock pipeline (exported values) ----
    public readonly double[] BaseSlip = new double[W];
    public readonly double[] Slip = new double[W];
    public readonly double[] Lock = new double[W];
    public readonly double[] Abs = new double[W];
    public readonly double[] Tc = new double[W];
    public readonly double[] SlipBlend = new double[W];
    public readonly double[] LockBlend = new double[W];
    public readonly double[] SlipTc = new double[W];
    public readonly double[] LockAbs = new double[W];
    public readonly double[] Loads = new double[W];
    public double BaseSlipMono;
    public double BaseLockMono;
    public double BaseAbsMono;
    public double BaseTcMono;
    public double SlipMono;
    public double LockMono;
    public double SlipTcMono;
    public double LockAbsMono;

    // ---- Balance ----
    public string BalanceSourceName = string.Empty;
    public bool BalanceSupported;

    /// <summary>Resolved balance property paths (refreshed at ~1 Hz, only while the debug view is shown).</summary>
    public string BalanceResolution = string.Empty;

    public readonly BalanceOutputs Balance = new BalanceOutputs();
    public BalanceClassPreset ClassPresetAuto;
    public BalanceClassPreset? ClassPresetOverride;
    public readonly BalanceOverrides Overrides = new BalanceOverrides();
    public bool LearningLocked;

    // ---- Diagnostics ----
    public bool RecordingActive;
    public string RecordingPath = string.Empty;
    public string LastError = string.Empty;
    public long FrameCount;

    /// <summary>Exponential average of DataUpdate processing time in milliseconds.</summary>
    public double DataUpdateMs;

    /// <summary>Copies every field into <paramref name="t"/> (allocation-free).</summary>
    public void CopyTo(HapticsSnapshot t)
    {
        t.GameRunning = GameRunning;
        t.GameName = GameName;
        t.HasCar = HasCar;
        t.CarId = CarId;
        t.CarKey = CarKey;
        t.CarDisplayName = CarDisplayName;
        t.CarClass = CarClass;
        t.CarProfileVersion = CarProfileVersion;
        t.CarProfilePath = CarProfilePath;
        t.CarKeySource = CarKeySource;
        t.CarKeyProvisional = CarKeyProvisional;
        t.SlipSensitivity = SlipSensitivity;
        t.LockSensitivity = LockSensitivity;
        t.UndersteerSensitivity = UndersteerSensitivity;
        t.OversteerSensitivity = OversteerSensitivity;
        t.SlipSource = SlipSource;
        t.ResolvedSlipSource = ResolvedSlipSource;
        t.SlipDataAvailable = SlipDataAvailable;
        t.ShakeItSlipAvailable = ShakeItSlipAvailable;
        t.ShakeItLockAvailable = ShakeItLockAvailable;
        t.LockUsesShakeIt = LockUsesShakeIt;
        t.LockSynthesized = LockSynthesized;
        t.Detection = Detection;
        t.DetectSpeed = DetectSpeed;
        t.DetectLat = DetectLat;
        t.DetectBrake = DetectBrake;
        t.DetectSpeedOk = DetectSpeedOk;
        t.DetectCornerOk = DetectCornerOk;
        t.DetectBrakeOk = DetectBrakeOk;
        t.DetectFrames = DetectFrames;
        t.DetectMaxDelta = DetectMaxDelta;
        t.PresetName = PresetName;
        t.Preset = Preset;
        t.GameExportsAbs = GameExportsAbs;
        t.GameExportsTc = GameExportsTc;
        t.CarHasAbs = CarHasAbs;
        t.CarHasTc = CarHasTc;
        t.AbsLevel = AbsLevel;
        t.TcLevel = TcLevel;
        t.AggregateUsesTc = AggregateUsesTc;
        t.AggregateUsesAbs = AggregateUsesAbs;
        t.MaxSway = MaxSway;
        t.MaxSurge = MaxSurge;
        t.MaxDecel = MaxDecel;
        for (int i = 0; i < W; i++)
        {
            t.SlipPaths[i] = SlipPaths[i];
            t.LockPaths[i] = LockPaths[i];
            t.BaseSlip[i] = BaseSlip[i];
            t.Slip[i] = Slip[i];
            t.Lock[i] = Lock[i];
            t.Abs[i] = Abs[i];
            t.Tc[i] = Tc[i];
            t.SlipBlend[i] = SlipBlend[i];
            t.LockBlend[i] = LockBlend[i];
            t.SlipTc[i] = SlipTc[i];
            t.LockAbs[i] = LockAbs[i];
            t.Loads[i] = Loads[i];
        }

        t.BaseSlipMono = BaseSlipMono;
        t.BaseLockMono = BaseLockMono;
        t.BaseAbsMono = BaseAbsMono;
        t.BaseTcMono = BaseTcMono;
        t.SlipMono = SlipMono;
        t.LockMono = LockMono;
        t.SlipTcMono = SlipTcMono;
        t.LockAbsMono = LockAbsMono;
        t.BalanceSourceName = BalanceSourceName;
        t.BalanceSupported = BalanceSupported;
        t.BalanceResolution = BalanceResolution;
        Balance.CopyTo(t.Balance);
        t.ClassPresetAuto = ClassPresetAuto;
        t.ClassPresetOverride = ClassPresetOverride;
        Overrides.CopyTo(t.Overrides);
        t.LearningLocked = LearningLocked;
        t.RecordingActive = RecordingActive;
        t.RecordingPath = RecordingPath;
        t.LastError = LastError;
        t.FrameCount = FrameCount;
        t.DataUpdateMs = DataUpdateMs;
    }
}
