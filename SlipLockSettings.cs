using System.Collections.Generic;

namespace User.SlipLockPropertiesCalc
{
    public class GameCapabilities
    {
        public string WheelSpeedMode = "Unknown";
        public string ABSMode = "Unknown";
        public string TCMode = "Unknown";
    }

    /// <summary>
    /// Hardcoded per-game preprocessor tuning. Not user-editable.
    /// </summary>
    public class GamePreset
    {
        public double SlipLat, SlipLong;   // proxyL for slip channel
        public double LockLat, LockLong;   // proxyL for lock channel
        public double ABSLat, ABSLong;     // proxyL for ABS channel (always strong)
        public double TCLat, TCLong;       // proxyL for TC channel (always strong)
        public bool SynthLockFromSlip;     // true = derive lock from slip during braking
        public double SpeedFadeKmh;        // below this speed, slip/lock fades to zero linearly (0 = no fade)
        public bool InverseSlipLoad;       // true = divide slip by load instead of multiply
        public double PreGain;             // preprocessor gain % (0-100, scales output range, 100=no change)
        public double PreCut;              // preprocessor cut % (0-100, values below this become 0, then remap to 0-100)

        public GamePreset(double slipLat, double slipLong, double lockLat, double lockLong,
                          double absLat, double absLong, double tcLat, double tcLong, bool synthLock,
                          double speedFadeKmh = 0, bool inverseSlipLoad = false,
                          double preGain = 100, double preCut = 0)
        {
            SlipLat = slipLat; SlipLong = slipLong; LockLat = lockLat; LockLong = lockLong;
            ABSLat = absLat; ABSLong = absLong; TCLat = tcLat; TCLong = tcLong;
            SynthLockFromSlip = synthLock; SpeedFadeKmh = speedFadeKmh; InverseSlipLoad = inverseSlipLoad;
            PreGain = preGain; PreCut = preCut;
        }
    }

    public static class GamePresets
    {
        public static readonly Dictionary<string, GamePreset> Table = new Dictionary<string, GamePreset>
        {
            // iRacing: mono slip only, no per-wheel, no TC. Heavy proxyL.
            { "IRacing", new GamePreset(50, 50, 50, 50, 50, 50, 50, 50, true) },
            // ACC: ShakeIT slip goes positive for both spin and lock. Synth lock from braking context.
            { "AssettoCorsaCompetizione", new GamePreset(10, 10, 10, 10, 50, 50, 50, 50, true) },
            // AC: same as ACC
            { "AssettoCorsa", new GamePreset(10, 10, 10, 10, 50, 50, 50, 50, true) },
            // AC Evo: likely same as AC
            { "AssettoCorsaEvo", new GamePreset(10, 10, 10, 10, 50, 50, 50, 50, true) },
            // LMU: rotation-based slip already includes tire loading effects,
            // use inverse proxyL on slip to counteract. Speed fade below 70 km/h.
            // Precut 15% to remove noise floor, speed fade 70 km/h.
            { "LMU", new GamePreset(15, 15, 15, 15, 50, 50, 50, 50, true, 70, true, 100, 15) },
            // rFactor 2: same engine as LMU
            { "RFactor2", new GamePreset(15, 15, 15, 15, 50, 50, 50, 50, true, 70, true, 100, 15) },
            // AMS2
            { "AMS2", new GamePreset(15, 15, 15, 15, 50, 50, 50, 50, true) },
            // BeamNG
            { "BeamNGdrive", new GamePreset(15, 15, 15, 15, 50, 50, 50, 50, true) },
        };

        // Fallback for unknown games
        // Default: synth lock from slip (most games), moderate proxyL
        public static readonly GamePreset Default = new GamePreset(30, 30, 30, 30, 50, 50, 50, 50, true);
    }

    public class SlipLockSettings
    {
        public int SpeedWarningLevel = 100;

        // 4 separate pedal blend sliders
        public double SlipThrottleBlend = 20.0;
        public double TCThrottleBlend = 50.0;
        public double LockBrakeBlend = 20.0;
        public double ABSBrakeBlend = 50.0;

        // Per-channel thresholds
        public double SlipThreshold = 5.0;
        public double LockThreshold = 5.0;
        public double TCThreshold = 5.0;
        public double ABSThreshold = 5.0;

        // Pedal gates
        public bool GateSlipOnThrottle = false;
        public bool GateLockOnBrake = false;

        // Envelope shaping (ms)
        public double SlipAttackMs = 10;
        public double SlipReleaseMs = 100;
        public double LockAttackMs = 10;
        public double LockReleaseMs = 100;
        public double ABSAttackMs = 5;
        public double ABSReleaseMs = 50;
        public double TCAttackMs = 5;
        public double TCReleaseMs = 50;

        public Dictionary<string, GameCapabilities> GameCapabilities = new Dictionary<string, GameCapabilities>();
    }
}
