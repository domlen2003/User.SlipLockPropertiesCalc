using System.Globalization;
using User.SlipLockPropertiesCalc.Balance;
using User.SlipLockPropertiesCalc.Core;
using User.SlipLockPropertiesCalc.Telemetry;

namespace User.SlipLockPropertiesCalc.UI;

/// <summary>
/// Text for numbers and enums shown in the settings UI. Enum texts are constants (no allocation per refresh).
/// Numbers use the invariant culture so they match the SimHub slider number boxes (WPF formats with en-US).
/// </summary>
internal static class DisplayText
{
    /// <summary>Shown for unknown values (NaN, infinity, null).</summary>
    public const string Missing = "-";

    public const string Yes = "yes";
    public const string No = "no";

    /// <summary>Culture for every formatted number in the UI.</summary>
    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Formats a number with a .NET format string; non-finite values become <see cref="Missing"/>.</summary>
    public static string Number(double value, string format) =>
        MathUtil.IsFinite(value) ? value.ToString(format, Culture) : Missing;

    /// <summary>Formats a 0..1 fraction as a whole percentage, e.g. 0.456 → "46 %".</summary>
    public static string Percent(double fraction) =>
        MathUtil.IsFinite(fraction) ? (fraction * 100.0).ToString("0", Culture) + " %" : Missing;

    public static string YesNo(bool value) => value ? Yes : No;

    /// <summary>User-facing name of a slip source.</summary>
    public static string SlipSource(SlipSourceKind kind) => kind switch
    {
        SlipSourceKind.ShakeIt => "ShakeIT",
        SlipSourceKind.AccNative => "ACC native",
        SlipSourceKind.RFactorRotation => "wheel rotation (rF2/LMU)",
        SlipSourceKind.PerWheelSpeed => "per-wheel speed",
        _ => "none",
    };

    /// <summary>Why the balance outputs are (not) active, phrased for the status line.</summary>
    public static string Gate(BalanceGate gate) => gate switch
    {
        BalanceGate.Active => "active",
        BalanceGate.NoData => "no data",
        BalanceGate.UnsupportedSim => "not available for this sim",
        BalanceGate.NotOnTrack => "not on track",
        BalanceGate.Replay => "replay",
        BalanceGate.Paused => "paused",
        BalanceGate.PitLane => "pit lane",
        BalanceGate.Reverse => "reversing",
        BalanceGate.LowSpeed => "low speed",
        BalanceGate.Blanked => "settling after reset",
        BalanceGate.Contact => "contact",
        BalanceGate.Airborne => "airborne",
        BalanceGate.Frozen => "no new data",
        BalanceGate.SpinTimeout => "spinning",
        BalanceGate.Calibrating => "calibrating steering, drive a few corners",
        _ => Missing,
    };

    public static string Path(BalancePath path) => path switch
    {
        BalancePath.Model => "model",
        BalancePath.Direct => "direct",
        _ => "none",
    };

    public static string Source(ParamSource source) => source switch
    {
        ParamSource.Preset => "preset",
        ParamSource.Learned => "learned",
        ParamSource.Session => "session",
        ParamSource.Manual => "manual",
        _ => "default",
    };

    public static string ClassPreset(BalanceClassPreset preset) => preset switch
    {
        BalanceClassPreset.FormulaPrototype => "Formula / prototype",
        BalanceClassPreset.GT => "GT",
        BalanceClassPreset.RoadTouring => "Road / touring",
        BalanceClassPreset.RallyLoose => "Rally / loose surface",
        BalanceClassPreset.Oval => "Oval",
        _ => "None (generic)",
    };

    public static string Mode(BalanceMode mode) => mode switch
    {
        BalanceMode.ModelOnly => "Model only (yaw rate vs. steering)",
        BalanceMode.DirectOnly => "Direct only (tyre slip angles)",
        _ => "Auto (direct when available, else model)",
    };

    /// <summary>v1 wording for the ABS/TC "car has it" column.</summary>
    public static string CarCapability(TriState state) => state switch
    {
        TriState.Yes => "Yes",
        TriState.Unknown => "Unknown",
        _ => "No data",
    };

    /// <summary>"+1" / "-1" for runtime-verified sign corrections.</summary>
    public static string Sign(int sign) => sign < 0 ? "-1" : "+1";

    /// <summary>A sign that is still being verified.</summary>
    public static string SignVerifying(int sign) => sign < 0 ? "-1 (verifying)" : "+1 (verifying)";
}
