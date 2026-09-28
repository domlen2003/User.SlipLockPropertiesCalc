using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Telemetry;

/// <summary>
/// Every SimHub property path the Telemetry module reads, built once at type initialization so the per-frame
/// code only passes cached string instances to <see cref="ITelemetryReader"/>.
/// Per-wheel arrays are indexed like <see cref="Wheels"/> (FL, FR, RL, RR). Treat the arrays as read-only.
/// </summary>
internal static class PropertyPaths
{
    /// <summary>Prefix of the ShakeIT Bass Shakers "data export" properties.</summary>
    public const string ShakeItExportPrefix = "ShakeITBSV3Plugin.Export.";

    /// <summary>Prefix of SimHub's normalized game data.</summary>
    public const string GameDataPrefix = "DataCorePlugin.GameData.";

    /// <summary>Prefix of the raw, sim-specific telemetry objects.</summary>
    public const string GameRawDataPrefix = "DataCorePlugin.GameRawData.";

    /// <summary>ACC/AC shared-memory physics page.</summary>
    public const string AccPhysicsPrefix = GameRawDataPrefix + "Physics.";

    /// <summary>rFactor2/LMU player telemetry (<c>rF2VehicleTelemetry</c>).</summary>
    public const string RFactorTelemetryPrefix = GameRawDataPrefix + "CurrentPlayerTelemetry.";

    /// <summary>LMU-only native player telemetry (holds the livery-independent car model name).</summary>
    public const string LmuNativeTelemetryPrefix = GameRawDataPrefix + "PlayerNativeTelemetry.";

    /// <summary>iRacing telemetry object.</summary>
    public const string IRacingTelemetryPrefix = GameRawDataPrefix + "Telemetry.";

    /// <summary>TC level; the property existing at all means "the game exports TC" (v1 semantics).</summary>
    public const string TcLevel = GameDataPrefix + "TCLevel";

    /// <summary>ABS level; the property existing at all means "the game exports ABS" (v1 semantics).</summary>
    public const string AbsLevel = GameDataPrefix + "ABSLevel";

    /// <summary>LMU car model, <c>byte[30]</c> NUL-terminated ASCII (e.g. "Ligier JS P325"). Not livery specific.</summary>
    public const string LmuVehicleModel = LmuNativeTelemetryPrefix + "mVehicleModel";

    /// <summary>LMU vehicle class enum (<c>IP_VehicleClass</c>: Hypercar, LMP2, LMP3, GTE, GT3, ...).</summary>
    public const string LmuVehicleClass = LmuNativeTelemetryPrefix + "mVehicleClass";

    /// <summary>ShakeIT exported wheel slip (0..100), first slip candidate.</summary>
    public static readonly string[] ShakeItWheelSlip = PerWheelNamed(ShakeItExportPrefix + "WheelSlip.");

    /// <summary>ShakeIT exported slip proxy (0..100), second slip candidate.</summary>
    public static readonly string[] ShakeItProxySlip = PerWheelNamed(ShakeItExportPrefix + "proxyS.");

    /// <summary>ShakeIT exported wheel lock (0..100). Never read by v1; merged into the lock channel in v2.</summary>
    public static readonly string[] ShakeItWheelLock = PerWheelNamed(ShakeItExportPrefix + "WheelLock.");

    /// <summary>ACC native wheel slip (unsigned ratio), third slip candidate.</summary>
    public static readonly string[] AccWheelSlip = PerWheelIndexed(AccPhysicsPrefix + "WheelSlip", string.Empty);

    /// <summary>rFactor2/LMU wheel rotation in rad/s.</summary>
    public static readonly string[] RFactorWheelRotation = PerWheelIndexed(RFactorTelemetryPrefix + "mWheels", ".mRotation");

    /// <summary>rFactor2/LMU static undeflected tyre radius (byte, centimetres).</summary>
    public static readonly string[] RFactorWheelRadius = PerWheelIndexed(RFactorTelemetryPrefix + "mWheels", ".mStaticUndeflectedRadius");

    /// <summary>Per-wheel speed in m/s under the iRacing telemetry object (v1 first candidate; absent live).</summary>
    public static readonly string[] IRacingWheelSpeed =
    {
        IRacingTelemetryPrefix + "LFspeed",
        IRacingTelemetryPrefix + "RFspeed",
        IRacingTelemetryPrefix + "LRspeed",
        IRacingTelemetryPrefix + "RRspeed",
    };

    /// <summary>Bare per-wheel speed names (v1 second candidate).</summary>
    public static readonly string[] BareWheelSpeed = { "LFspeed", "RFspeed", "LRspeed", "RRspeed" };

    /// <summary>Slip candidates per wheel in v1 priority order: ShakeIT WheelSlip, ShakeIT proxyS, ACC native.</summary>
    public static readonly string[][] SlipCandidates = Candidates(ShakeItWheelSlip, ShakeItProxySlip, AccWheelSlip);

    /// <summary>Wheel-speed candidates per wheel in v1 priority order.</summary>
    public static readonly string[][] WheelSpeedCandidates = Candidates(IRacingWheelSpeed, BareWheelSpeed);

    /// <summary>
    /// The v1 diagnostic "SCAN" list (logged once per game by the debug file log and included in property dumps).
    /// </summary>
    public static readonly string[] DiagnosticScanPaths =
    {
        ShakeItWheelSlip[Wheels.FrontLeft],
        ShakeItWheelLock[Wheels.FrontLeft],
        IRacingWheelSpeed[Wheels.FrontLeft],
        BareWheelSpeed[Wheels.FrontLeft],
        AccWheelSlip[Wheels.FrontLeft],
        AccWheelSlip[Wheels.FrontRight],
        AccPhysicsPrefix + "WheelAngularSpeed01",
        RFactorTelemetryPrefix + "mWheels01.mGripFract",
        RFactorWheelRotation[Wheels.FrontLeft],
        RFactorWheelRadius[Wheels.FrontLeft],
        RFactorTelemetryPrefix + "mWheels01.mTireLoad",
        GameDataPrefix + "ABSActive",
        GameDataPrefix + "TCActive",
        TcLevel,
        AbsLevel,
        LmuVehicleModel,
        LmuVehicleClass,
    };

    /// <summary>Builds <c>prefix + "FrontLeft"</c> etc.</summary>
    private static string[] PerWheelNamed(string prefix)
    {
        var paths = new string[Wheels.Count];
        for (int i = 0; i < Wheels.Count; i++)
        {
            paths[i] = prefix + Wheels.Names[i];
        }

        return paths;
    }

    /// <summary>Builds <c>prefix + "01" + suffix</c> etc. (SimHub's 1-based, two-digit array convention).</summary>
    private static string[] PerWheelIndexed(string prefix, string suffix)
    {
        var paths = new string[Wheels.Count];
        for (int i = 0; i < Wheels.Count; i++)
        {
            paths[i] = prefix + Wheels.RawSuffixes[i] + suffix;
        }

        return paths;
    }

    /// <summary>Transposes per-source wheel arrays into per-wheel candidate lists (source order = priority).</summary>
    private static string[][] Candidates(params string[][] sources)
    {
        var result = new string[Wheels.Count][];
        for (int wheel = 0; wheel < Wheels.Count; wheel++)
        {
            result[wheel] = new string[sources.Length];
            for (int source = 0; source < sources.Length; source++)
            {
                result[wheel][source] = sources[source][wheel];
            }
        }

        return result;
    }
}
