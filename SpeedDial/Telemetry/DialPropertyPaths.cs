using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>
/// Every SimHub property path the SpeedDial telemetry readers use, as full constant strings (built at compile time,
/// so reading never concatenates). SpeedDial keeps its own copies instead of referencing <c>Haptics</c>.
/// </summary>
/// <remarks>
/// Spellings were verified by reflection on the installed SimHub (<c>RfactorReader.LMU.TelemInfoV01</c>,
/// <c>ACSharedMemory.ACC/ACR/ACEVO.MMFModels.*</c>) and on SimHub's iRacing sample data. The AC EVO nested
/// <c>Graphics.electronics.*</c> paths assume SimHub flattens nested structs with a dot (as it does for
/// <c>mWheels01.mSurfaceType</c>); the camelCase <c>Physics</c> fields stay as fallback candidates.
/// </remarks>
internal static class DialPropertyPaths
{
    /// <summary>SimHub's normalized, game-independent data.</summary>
    public const string GameDataPrefix = "DataCorePlugin.GameData.";

    /// <summary>Raw, sim-specific telemetry objects.</summary>
    public const string GameRawDataPrefix = "DataCorePlugin.GameRawData.";

    // ---- LMU native player telemetry (bytes; brake bias is the rear fraction as double) ----

    /// <summary>LMU native player telemetry prefix (shared with the car identity resolver).</summary>
    public const string LmuPrefix = CarIdentityResolver.LmuNativeTelemetryPrefix;

    /// <summary>LMU "TC" level.</summary>
    public const string LmuTc = LmuPrefix + "mTC";

    /// <summary>LMU highest "TC" level of the car (0 = not adjustable).</summary>
    public const string LmuTcMax = LmuPrefix + "mTCMax";

    /// <summary>LMU "TC Power Cut" level.</summary>
    public const string LmuTcCut = LmuPrefix + "mTCCut";

    /// <summary>LMU highest "TC Power Cut" level.</summary>
    public const string LmuTcCutMax = LmuPrefix + "mTCCutMax";

    /// <summary>LMU "TC Slip Angle" level.</summary>
    public const string LmuTcSlip = LmuPrefix + "mTCSlip";

    /// <summary>LMU highest "TC Slip Angle" level.</summary>
    public const string LmuTcSlipMax = LmuPrefix + "mTCSlipMax";

    /// <summary>LMU ABS level.</summary>
    public const string LmuAbs = LmuPrefix + "mABS";

    /// <summary>LMU highest ABS level.</summary>
    public const string LmuAbsMax = LmuPrefix + "mABSMax";

    /// <summary>LMU share of braking on the rear axle (0..1); front percent = (1 - x)·100.</summary>
    public const string LmuRearBrakeBias = LmuPrefix + "mRearBrakeBias";

    // ---- iRacing telemetry (in-car adjustments; a car without the control has no property) ----

    /// <summary>iRacing telemetry prefix.</summary>
    public const string IRacingPrefix = GameRawDataPrefix + "Telemetry.";

    /// <summary>iRacing traction control setting.</summary>
    public const string IRacingTc = IRacingPrefix + "dcTractionControl";

    /// <summary>iRacing second traction control setting (dynamic variable, used by SimHub dashboards).</summary>
    public const string IRacingTc2 = IRacingPrefix + "dcTractionControl2";

    /// <summary>iRacing ABS setting.</summary>
    public const string IRacingAbs = IRacingPrefix + "dcABS";

    /// <summary>iRacing brake bias, front percent.</summary>
    public const string IRacingBrakeBias = IRacingPrefix + "dcBrakeBias";

    // ---- ACC / AC Rally (Graphics page levels, Physics page brake bias) ----

    /// <summary>AC family graphics page prefix.</summary>
    public const string AccGraphicsPrefix = GameRawDataPrefix + "Graphics.";

    /// <summary>AC family physics page prefix.</summary>
    public const string AccPhysicsPrefix = GameRawDataPrefix + "Physics.";

    /// <summary>ACC / AC Rally current TC level (int).</summary>
    public const string AccTc = AccGraphicsPrefix + "TC";

    /// <summary>ACC / AC Rally current TC cut level (int).</summary>
    public const string AccTcCut = AccGraphicsPrefix + "TCCut";

    /// <summary>ACC / AC Rally current ABS level (int).</summary>
    public const string AccAbs = AccGraphicsPrefix + "ABS";

    /// <summary>ACC / AC Rally brake bias, raw front fraction (0..1) without the car-specific display offset.</summary>
    public const string AccBrakeBias = AccPhysicsPrefix + "BrakeBias";

    // ---- AC EVO (lower-camel physics page; integer levels in the graphics page's electronics struct) ----

    /// <summary>AC EVO current electronics levels (<c>SMEvoElectronics</c>, sbyte levels).</summary>
    public const string EvoElectronicsPrefix = AccGraphicsPrefix + "electronics.";

    /// <summary>AC EVO per-car upper limits of the electronics levels.</summary>
    public const string EvoMaxLimitPrefix = AccGraphicsPrefix + "electronics_max_limit.";

    /// <summary>AC EVO TC level.</summary>
    public const string EvoTcLevel = EvoElectronicsPrefix + "tc_level";

    /// <summary>AC EVO TC cut level.</summary>
    public const string EvoTcCutLevel = EvoElectronicsPrefix + "tc_cut_level";

    /// <summary>AC EVO ABS level.</summary>
    public const string EvoAbsLevel = EvoElectronicsPrefix + "abs_level";

    /// <summary>AC EVO electronics brake bias (unit unverified: fraction or percent, see <see cref="DialValueTransform.FractionOrPercent"/>).</summary>
    public const string EvoElectronicsBrakeBias = EvoElectronicsPrefix + "brake_bias";

    /// <summary>AC EVO highest TC level of the car.</summary>
    public const string EvoTcMax = EvoMaxLimitPrefix + "tc_level";

    /// <summary>AC EVO highest TC cut level of the car.</summary>
    public const string EvoTcCutMax = EvoMaxLimitPrefix + "tc_cut_level";

    /// <summary>AC EVO highest ABS level of the car.</summary>
    public const string EvoAbsMax = EvoMaxLimitPrefix + "abs_level";

    /// <summary>AC EVO physics-page TC (fallback; the AC legacy page field, meaning unverified).</summary>
    public const string EvoPhysicsTc = AccPhysicsPrefix + "tc";

    /// <summary>AC EVO physics-page ABS (fallback; meaning unverified).</summary>
    public const string EvoPhysicsAbs = AccPhysicsPrefix + "abs";

    /// <summary>AC EVO brake bias, raw front fraction (0..1) like ACC.</summary>
    public const string EvoPhysicsBrakeBias = AccPhysicsPrefix + "brakeBias";

    // ---- Generic SimHub normalized data ----

    /// <summary>SimHub normalized TC level.</summary>
    public const string GenericTcLevel = GameDataPrefix + "TCLevel";

    /// <summary>SimHub normalized ABS level.</summary>
    public const string GenericAbsLevel = GameDataPrefix + "ABSLevel";

    /// <summary>SimHub normalized brake bias, front percent (0 when the game does not report it).</summary>
    public const string GenericBrakeBias = GameDataPrefix + "BrakeBias";
}
