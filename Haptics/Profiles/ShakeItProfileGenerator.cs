using System;
using System.IO;
using DivebombLogistics.Core.Persistence;
using DivebombLogistics.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DivebombLogistics.Haptics.Profiles;

/// <summary>Outcome of writing a ShakeIT profile file.</summary>
internal sealed class ProfileWriteResult
{
    public ProfileWriteResult(bool success, string path, string message)
    {
        Success = success;
        Path = path;
        Message = message;
    }

    public bool Success { get; }

    /// <summary>The file written (or attempted).</summary>
    public string Path { get; }

    /// <summary>Status text for the UI.</summary>
    public string Message { get; }
}

/// <summary>
/// Generates importable ShakeIT profiles (<c>.siprofile</c> JSON):
/// <list type="bullet">
/// <item><b>Data export</b> (Bass shakers tab): wheel slip/lock effects with outputs disabled, used only to export
/// <c>ShakeITBSV3Plugin.Export.WheelSlip.*</c>/<c>WheelLock.*</c>, the plugin's preferred slip source.</item>
/// <item><b>Haptic pedals</b> (Motors tab): custom effects driving throttle/brake pedal motors from the
/// <c>DLP.SlipLock.*.Mono</c> channels, plus a gear-shift pulse on both pedals.</item>
/// <item><b>Balance</b> (Motors tab): understeer on the front corners, oversteer on the rear corners.</item>
/// </list>
/// Since v3 (DLP) the file names, profile names and formula prefix say "DLP" instead of "SlipLock"/
/// "SlipLockPropertiesCalc" (users re-import the profiles once). Apart from that the data export is identical to the
/// v1 output (same keys, values and types; only the ids are fresh), and the haptic pedal profile keeps v1's effects and
/// settings with three deliberate differences:
/// <list type="bullet">
/// <item>Channels follow the user's pedal set (0 clutch, 1 brake, 2 throttle) as in their exported ShakeIT profile;
/// v1 assumed throttle on channel 0.</item>
/// <item>The disabled "Slip*Throttle"/"Lock*Brake" effects keep their pedal channel enabled, so enabling the effect
/// in ShakeIT is enough (v1 accidentally disabled the channel together with the effect).</item>
/// <item>A gear-shift effect (<c>GearEffectContainer</c>) is appended, copied from the user's exported profile.</item>
/// </list>
/// </summary>
internal static class ShakeItProfileGenerator
{
    public const string DataExportFileName = "DLP_DataExport.siprofile";
    public const string HapticPedalFileName = "DLP_HapticPedals.siprofile";
    public const string BalanceFileName = "DLP_Balance.siprofile";

    public const string DataExportSavedMessage = "Saved! Import in ShakeIT and restart.";
    public const string HapticPedalSavedMessage = "Haptic pedal profile saved! Import in ShakeIT Motors tab.";
    public const string BalanceSavedMessage = "Balance profile saved! Import in ShakeIT Motors tab.";
    public const string ErrorMessagePrefix = "Error: ";

    /// <summary>SimHub prefixes this plugin's properties with the plugin class name (<c>DLP.</c>).</summary>
    public const string PropertyPrefix = DlpNames.PropertyPrefix;

    /// <summary>Profile names shown in ShakeIT.</summary>
    public const string DataExportProfileName = "DLP Data Export";
    public const string HapticPedalProfileName = "DLP Haptic Pedals";
    public const string BalanceProfileName = "DLP Balance";

    /// <summary>Description of the data export's effect group (unchanged since v1).</summary>
    private const string DataExportGroupDescription = "SlipLock Data Export";

    // ShakeIT enum values stored as numbers in profile files.
    private const int OutputModeBassShakers = 1;
    private const int OutputModeMotors = 3;

    private const string AggregationCorners = "Corners";
    private const string AggregationMono = "Mono";

    private const double ProfileGlobalGain = 50.0;
    private const int AutoCalibrationRatio = 100;

    /// <summary>The data export group passes its effects through unchanged.</summary>
    private const double GroupGain = 100.0;

    private const double DataExportEffectGain = 50.0;
    private const int DataExportFrequencyHz = 50;
    private const int SlipPedalFilterPercent = 10;
    private const int LockBrakeFilterPercent = 20;
    private const double LockSensibility = 50.0;

    private const double MotorEffectGain = 100.0;
    private const int ThrottleMotorFrequencyHz = 30;
    private const int BrakeMotorFrequencyHz = 25;
    private const int UndersteerFrequencyHz = 40;
    private const int OversteerFrequencyHz = 35;

    /// <summary>Default tone output settings; only the frequency varies between effects.</summary>
    private const int DefaultHighFrequencyHz = 50;
    private const int DefaultWhiteNoise = 10;

    /// <summary>"Never loaded" time stamp ShakeIT writes into fresh profiles.</summary>
    private const string NeverLoaded = "0001-01-01T00:00:00";

    /// <summary>Balance outputs are 0..1; ShakeIT effect formulas expect 0..100.</summary>
    private const string BalanceToPercent = " * 100";

    // Gear-shift effect, values copied from the user's exported "SlipLock Haptic Pedals" profile
    // (Documents\SimHub\Any Game - SlipLock Haptic Pedals old.siprofile). The gain is the slider value as ShakeIT
    // stored it, kept exact so the regenerated profile feels the same.
    private const double GearShiftGain = 37.04761904761914;
    private const int GearShiftMaxFeedbackRpmPercent = 90;
    private const int GearShiftMinFeedbackRpmPercent = 50;

    /// <summary>ShakeIT gear mode as stored in the user's profile.</summary>
    private const int GearShiftMode = 2;

    private const double GearShiftNeutralDebounceMs = 200.0;
    private const double GearShiftEngagingDebounceMs = 1000.0;
    private const int GearShiftPulseDurationMs = 90;
    private const int GearShiftFrequencyHz = 15;

    /// <summary>
    /// Pedal motor channels of the user's haptic pedal set: clutch, brake, throttle from left to right. Taken from
    /// their exported ShakeIT profile (throttle effect on channel 2, brake on 1).
    /// </summary>
    private enum PedalChannel
    {
        Clutch = 0,
        Brake = 1,
        Throttle = 2,
    }

    /// <summary>Default output folder: <c>Documents\SimHub</c> (v1 location).</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimHub");

    /// <summary>Writes <see cref="DataExportFileName"/> into <paramref name="directory"/>. Never throws.</summary>
    public static ProfileWriteResult WriteDataExportProfile(string directory) =>
        Write(directory, DataExportFileName, BuildDataExportProfile(NewId), DataExportSavedMessage);

    /// <summary>Writes <see cref="HapticPedalFileName"/> into <paramref name="directory"/>. Never throws.</summary>
    public static ProfileWriteResult WriteHapticPedalProfile(string directory) =>
        Write(directory, HapticPedalFileName, BuildHapticPedalProfile(NewId), HapticPedalSavedMessage);

    /// <summary>Writes <see cref="BalanceFileName"/> into <paramref name="directory"/>. Never throws.</summary>
    public static ProfileWriteResult WriteBalanceProfile(string directory) =>
        Write(directory, BalanceFileName, BuildBalanceProfile(NewId), BalanceSavedMessage);

    /// <summary>
    /// Bass-shaker profile whose slip and lock effects only export ShakeIT's per-wheel values
    /// (<c>ExportProperty</c> true, <c>DisableOutput</c> true), without driving any device.
    /// </summary>
    /// <param name="newId">Container/profile id factory (GUID strings; deterministic in tests).</param>
    internal static JObject BuildDataExportProfile(Func<string> newId)
    {
        var wheelSlip = new JObject
        {
            ["ContainerType"] = "WheelsSlipContainer",
            ["IsEnabled"] = true,
            ["Gain"] = DataExportEffectGain,
            ["BrakeFilter"] = SlipPedalFilterPercent,
            ["MuteWhenLockEffectIsActive"] = false,
            ["ThrottleFilter"] = SlipPedalFilterPercent,
            ["UseBrakeFilter"] = false,
            ["UseThrottleFilter"] = false,
            ["UseLegacyIracingAlgorythm"] = false,
            ["ContainerId"] = newId(),
            ["AggregationMode"] = AggregationCorners,
            ["Filter"] = GammaFilter(),
            ["Output"] = ExportOnlyOutput("WheelSlip"),
        };

        var wheelLock = new JObject
        {
            ["ContainerType"] = "WheelsLockContainer",
            ["IsEnabled"] = true,
            ["Gain"] = DataExportEffectGain,
            ["IsLock"] = true,
            ["UseLegacyIracingAlgorythm"] = false,
            ["LockSensibility"] = LockSensibility,
            ["BrakeFilter"] = LockBrakeFilterPercent,
            ["ContainerId"] = newId(),
            ["AggregationMode"] = AggregationCorners,
            ["Filter"] = GammaFilter(),
            ["Output"] = ExportOnlyOutput("WheelLock"),
        };

        var group = new JObject
        {
            ["ContainerType"] = "GroupContainer",
            ["IsEnabled"] = true,
            ["Gain"] = GroupGain,
            ["Description"] = DataExportGroupDescription,
            ["EffectsContainers"] = new JArray(wheelSlip, wheelLock),
            ["ContainerId"] = newId(),
            ["Filter"] = null,
            ["Output"] = null,
        };

        var profile = ProfileHeader();
        profile["EffectsContainers"] = new JArray(group);
        profile["AutoCalibrationRatio2"] = AutoCalibrationRatio;
        profile["OutputMode"] = OutputModeBassShakers;
        profile["GlobalGain"] = ProfileGlobalGain;
        profile["UseProfileGain"] = false;
        profile["Name"] = DataExportProfileName;
        profile["ProfileId"] = newId();
        profile["GameCode"] = null;
        profile["CarChoice"] = null;
        return profile;
    }

    /// <summary>
    /// Motors profile: SlipTC → throttle pedal, LockABS → brake pedal (enabled), the pure Slip*Throttle and
    /// Lock*Brake blends as disabled alternatives, and a gear-shift pulse on brake and throttle.
    /// </summary>
    /// <param name="newId">Container/profile id factory.</param>
    internal static JObject BuildHapticPedalProfile(Func<string> newId)
    {
        var effects = new JArray(
            PedalEffect(newId, "SlipTC Aggregate (throttle)", "SlipLock.SlipTC", PedalChannel.Throttle, ThrottleMotorFrequencyHz, enabled: true),
            PedalEffect(newId, "LockABS Aggregate (brake)", "SlipLock.LockABS", PedalChannel.Brake, BrakeMotorFrequencyHz, enabled: true),
            PedalEffect(newId, "Slip*Throttle (throttle)", "SlipLock.SlipBlend", PedalChannel.Throttle, ThrottleMotorFrequencyHz, enabled: false),
            PedalEffect(newId, "Lock*Brake (brake)", "SlipLock.LockBlend", PedalChannel.Brake, BrakeMotorFrequencyHz, enabled: false),
            GearShiftEffect(newId));

        return MotorsProfile(effects, HapticPedalProfileName, newId);
    }

    /// <summary>
    /// Motors profile: <c>Balance.Understeer</c> on the front corners, <c>Balance.Oversteer</c> on the rear corners.
    /// No channel map, so ShakeIT's own corner assignment of the user's devices applies.
    /// </summary>
    /// <param name="newId">Container/profile id factory.</param>
    internal static JObject BuildBalanceProfile(Func<string> newId)
    {
        string understeer = "[" + PropertyPrefix + "Balance.Understeer]" + BalanceToPercent;
        string oversteer = "[" + PropertyPrefix + "Balance.Oversteer]" + BalanceToPercent;

        var effects = new JArray(
            CustomEffect(newId, "Understeer (front)", understeer, understeer, string.Empty, string.Empty, channelMap: null, AggregationCorners, UndersteerFrequencyHz, enabled: true),
            CustomEffect(newId, "Oversteer (rear)", string.Empty, string.Empty, oversteer, oversteer, channelMap: null, AggregationCorners, OversteerFrequencyHz, enabled: true));

        return MotorsProfile(effects, BalanceProfileName, newId);
    }

    private static ProfileWriteResult Write(string directory, string fileName, JObject profile, string successMessage)
    {
        string path = null;
        try
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, fileName);
            JsonFile.WriteAllTextAtomic(path, profile.ToString(Formatting.Indented));
            return new ProfileWriteResult(true, path, successMessage);
        }
        catch (Exception ex)
        {
            // Directory missing/readonly, file open in another program...: report to the UI, never crash SimHub.
            return new ProfileWriteResult(false, path, ErrorMessagePrefix + ex.Message);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString();

    /// <summary>Leading members shared by every profile.</summary>
    private static JObject ProfileHeader() => new JObject
    {
        ["CarChoices"] = new JArray(),
        ["IncludeOutputSettingsInProfile"] = false,
        ["UnmuteEffectsAfterSimhubRestart"] = true,
    };

    private static JObject MotorsProfile(JArray effects, string name, Func<string> newId)
    {
        JObject profile = ProfileHeader();
        profile["EffectsContainers"] = effects;
        profile["AutoCalibrationRatio2"] = AutoCalibrationRatio;
        profile["OutputMode"] = OutputModeMotors;
        profile["GlobalGain"] = ProfileGlobalGain;
        profile["UseProfileGain"] = false;
        profile["LastLoaded"] = NeverLoaded;
        profile["Name"] = name;
        profile["ProfileId"] = newId();
        profile["GameCode"] = null;
        profile["CarChoice"] = null;
        return profile;
    }

    /// <summary>A mono pedal effect reading a SlipLock <c>.Mono</c> channel, routed to one pedal motor.</summary>
    private static JObject PedalEffect(Func<string> newId, string description, string channel, PedalChannel pedal, int frequencyHz, bool enabled)
    {
        string formula = "[" + PropertyPrefix + channel + ".Mono]";
        return CustomEffect(newId, description, formula, string.Empty, string.Empty, string.Empty, ChannelMap(pedal), AggregationMono, frequencyHz, enabled);
    }

    private static JObject CustomEffect(
        Func<string> newId,
        string description,
        string frontLeft,
        string frontRight,
        string rearLeft,
        string rearRight,
        JObject channelMap,
        string aggregationMode,
        int frequencyHz,
        bool enabled)
    {
        var effect = new JObject
        {
            ["ContainerType"] = "CustomEffectContainer",
            ["IsEnabled"] = enabled,
            ["Gain"] = MotorEffectGain,
            ["Description"] = description,
            ["FrontLeftFormula"] = Formula(frontLeft),
            ["FrontRightFormula"] = Formula(frontRight),
            ["RearLeftFormula"] = Formula(rearLeft),
            ["RearRightFormula"] = Formula(rearRight),
            ["ForceFrequencies"] = false,
            ["FrontLeftFrequencyFormula"] = Formula(string.Empty),
            ["FrontRightFrequencyFormula"] = Formula(string.Empty),
            ["RearLeftFrequencyFormula"] = Formula(string.Empty),
            ["RearRightFrequencyFormula"] = Formula(string.Empty),
            ["AlwaysExecute"] = false,
        };

        if (channelMap != null)
        {
            effect["SettingsStore"] = channelMap;
        }

        effect["ContainerId"] = newId();
        effect["AggregationMode"] = aggregationMode;
        effect["Filter"] = GammaFilter();
        effect["Output"] = ToneOutput(frequencyHz);
        return effect;
    }

    /// <summary>
    /// ShakeIT's built-in gear-shift effect: a short 15 Hz pulse on brake and throttle when a gear engages, neutral
    /// ignored. Member order and value types match the user's exported profile exactly.
    /// </summary>
    private static JObject GearShiftEffect(Func<string> newId) => new JObject
    {
        ["ContainerType"] = "GearEffectContainer",
        ["IsEnabled"] = true,
        ["Gain"] = GearShiftGain,
        ["ModulateGainUsingRpms"] = false,
        ["MaxFeedbackRpmPercent"] = GearShiftMaxFeedbackRpmPercent,
        ["MinFeedbackRpmPercent"] = GearShiftMinFeedbackRpmPercent,
        ["GearMode"] = GearShiftMode,
        ["AlwaysIgnoreNeutral"] = false,
        ["IgnoreNeutral"] = true,
        ["NeutralDebouningTime"] = GearShiftNeutralDebounceMs,
        ["EngagingDebouningTime"] = GearShiftEngagingDebounceMs,
        ["SettingsStore"] = ChannelMap(PedalChannel.Brake, PedalChannel.Throttle),
        ["ContainerId"] = newId(),
        ["Filter"] = new JObject
        {
            ["Duration"] = GearShiftPulseDurationMs,
            ["FilterType"] = "PulseFilter",
        },
        ["Output"] = new JObject
        {
            ["UsePrehemptiveMode"] = true,
            ["Frequency"] = GearShiftFrequencyHz,
            ["OutputType"] = "SingleToneOutput",
        },
    };

    private static JObject Formula(string expression) => new JObject { ["Expression"] = expression };

    /// <summary>
    /// Device channel activation: only the given pedals' motor channels (see <see cref="PedalChannel"/>) are on.
    /// Deliberate fix of v1: the channel is enabled independently of the effect's own <c>IsEnabled</c>. v1 built
    /// the disabled alternatives (Slip*Throttle, Lock*Brake) by replacing every <c>"IsEnabled":true</c> in the effect
    /// JSON, which also switched off their pedal channel, so enabling such an effect in ShakeIT produced no output
    /// until the channel was found and enabled too.
    /// </summary>
    private static JObject ChannelMap(params PedalChannel[] pedals)
    {
        var channels = new JObject
        {
            ["0"] = new JObject { ["IsEnabled"] = Array.IndexOf(pedals, PedalChannel.Clutch) >= 0 },
            ["1"] = new JObject { ["IsEnabled"] = Array.IndexOf(pedals, PedalChannel.Brake) >= 0 },
            ["2"] = new JObject { ["IsEnabled"] = Array.IndexOf(pedals, PedalChannel.Throttle) >= 0 },
        };

        var activation = new JObject
        {
            ["Channels"] = new JObject { ["All"] = new JObject { ["Channels"] = channels } },
            ["TypeName"] = "DeviceChannelActivationSettings",
        };

        return new JObject { ["Settings"] = new JArray(activation) };
    }

    /// <summary>Neutral gamma filter (gamma 1, no gain change, no threshold).</summary>
    private static JObject GammaFilter() => new JObject
    {
        ["GammaValue"] = 1.0,
        ["InputGain"] = 100.0,
        ["MinimumForce"] = 0,
        ["Threshold"] = 0,
        ["FilterType"] = "GammaFilter",
    };

    private static JObject ToneOutput(int frequencyHz)
    {
        JObject output = ToneOutputBase();
        output["Frequency"] = frequencyHz;
        output["OutputType"] = "ToneOutput";
        return output;
    }

    /// <summary>Tone output that drives nothing but exports the effect value as <c>ShakeITBSV3Plugin.Export.&lt;name&gt;.*</c>.</summary>
    private static JObject ExportOnlyOutput(string propertyName)
    {
        JObject output = ToneOutputBase();
        output["Frequency"] = DataExportFrequencyHz;
        output["PropertyName"] = propertyName;
        output["ExportProperty"] = true;
        output["DisableOutput"] = true;
        output["OutputType"] = "ToneOutput";
        return output;
    }

    private static JObject ToneOutputBase() => new JObject
    {
        ["UseHighFrequency"] = false,
        ["HighFrequency"] = DefaultHighFrequencyHz,
        ["WhiteNoise"] = DefaultWhiteNoise,
        ["UseWhiteNoise"] = false,
        ["FrequencyBasedOnPreFilter"] = false,
        ["UsePrehemptiveMode"] = false,
    };
}
