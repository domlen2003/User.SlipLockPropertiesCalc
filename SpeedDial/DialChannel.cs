using System;
using System.Collections.Generic;
using System.Globalization;
using DivebombLogistics.Core;

namespace DivebombLogistics.SpeedDial;

// Contract file (SpeedDial Stage B): the channel enum, its kind and the static registry live together so a future
// channel is added in one place (enum member at the end + one entry in every registry table + telemetry readers).

/// <summary>
/// A car setting SpeedDial can dial through Control Mapper role presses. The numeric value is the index into every
/// per-channel array (<see cref="DialChannels.Count"/> entries). COMPATIBILITY: append new channels at the end only;
/// files store channels by <see cref="DialChannels.Id"/>, never by number.
/// </summary>
public enum DialChannel
{
    /// <summary>Traction control level (LMU "TC", iRacing <c>dcTractionControl</c>, ACC <c>TC</c>).</summary>
    Tc1 = 0,

    /// <summary>Second TC map: LMU "TC Power Cut", iRacing <c>dcTractionControl2</c>, ACC <c>TCCut</c>.</summary>
    Tc2,

    /// <summary>Third TC map: LMU "TC Slip Angle" (no equivalent in iRacing/ACC).</summary>
    Tc3,

    /// <summary>ABS level.</summary>
    Abs,

    /// <summary>Brake bias, front share in percent (ACC: raw front fraction × 100, without the car-specific display offset).</summary>
    BrakeBias,
}

/// <summary>How a channel's value moves per role press.</summary>
public enum DialChannelKind
{
    /// <summary>Whole-number levels (TC, ABS): compared after rounding.</summary>
    Integer = 0,

    /// <summary>Continuous value (brake bias): compared with a tolerance.</summary>
    Continuous,
}

/// <summary>
/// Static registry of the dial channels: stable JSON ids, UI names, value kind, display format, comparison tolerance,
/// sane value range and the default Control Mapper roles (the user's own role names). Every lookup is
/// allocation-free except <see cref="FormatValue"/> (UI only). Invalid enum values yield empty strings / safe
/// defaults instead of throwing (never throw on the data thread).
/// </summary>
internal static class DialChannels
{
    /// <summary>Number of channels; length of every per-channel array.</summary>
    public const int Count = 5;

    /// <summary>JSON id of <see cref="DialChannel.Tc1"/>. COMPATIBILITY: ids are file keys, never rename.</summary>
    public const string IdTc1 = "TC1";

    /// <summary>JSON id of <see cref="DialChannel.Tc2"/>.</summary>
    public const string IdTc2 = "TC2";

    /// <summary>JSON id of <see cref="DialChannel.Tc3"/>.</summary>
    public const string IdTc3 = "TC3";

    /// <summary>JSON id of <see cref="DialChannel.Abs"/>.</summary>
    public const string IdAbs = "ABS";

    /// <summary>JSON id of <see cref="DialChannel.BrakeBias"/>.</summary>
    public const string IdBrakeBias = "BB";

    /// <summary>Default tolerance of brake bias in percent points (continuous channels).</summary>
    public const double BrakeBiasTolerance = 0.05;

    /// <summary>Tolerance reported for integer channels: they match when equal after rounding (|Δ| &lt; 0.5).</summary>
    public const double IntegerTolerance = 0.5;

    /// <summary>Upper sanity bound of integer channels (LMU reports bytes).</summary>
    public const double MaxIntegerValue = 255.0;

    /// <summary>Upper bound of brake bias (front percent).</summary>
    public const double MaxBrakeBiasPercent = 100.0;

    /// <summary>Continuous values are stored rounded to this many decimals (removes float noise like 54.2000000001).</summary>
    public const int ContinuousDecimals = 2;

    /// <summary>Text shown by <see cref="FormatValue"/> for an unknown value.</summary>
    public const string MissingValueText = "-";

    private const string IntegerFormat = "0";
    private const string PercentFormat = "0.0 '%'";

    private static readonly DialChannel[] AllChannels =
    {
        DialChannel.Tc1, DialChannel.Tc2, DialChannel.Tc3, DialChannel.Abs, DialChannel.BrakeBias,
    };

    private static readonly string[] Ids = { IdTc1, IdTc2, IdTc3, IdAbs, IdBrakeBias };

    private static readonly string[] DisplayNames = { "TC", "TC2 (Cut)", "TC3 (Slip)", "ABS", "Brake bias" };

    private static readonly string[] ShortNames = { "TC", "TC2", "TC3", "ABS", "BB" };

    private static readonly DialChannelKind[] Kinds =
    {
        DialChannelKind.Integer, DialChannelKind.Integer, DialChannelKind.Integer, DialChannelKind.Integer,
        DialChannelKind.Continuous,
    };

    private static readonly string[] Formats = { IntegerFormat, IntegerFormat, IntegerFormat, IntegerFormat, PercentFormat };

    private static readonly double[] Tolerances =
    {
        IntegerTolerance, IntegerTolerance, IntegerTolerance, IntegerTolerance, BrakeBiasTolerance,
    };

    private static readonly double[] MaxValues =
    {
        MaxIntegerValue, MaxIntegerValue, MaxIntegerValue, MaxIntegerValue, MaxBrakeBiasPercent,
    };

    /// <summary>The user's Control Mapper output roles (PluginsData\Common\ControlMapperPlugin.GeneralSettingsV2.json).</summary>
    private static readonly string[] IncreaseRoles = { "TractionControl+", "TC_PowerCut+", "TC_SlipAngle+", "ABS+", "BrakeBalanceFront" };

    private static readonly string[] DecreaseRoles = { "TractionControl-", "TC_PowerCut-", "TC_SlipAngle-", "ABS-", "BrakeBalanceRear" };

    /// <summary>Every channel in enum (default dial) order. The array itself is returned: callers must not cast and modify it.</summary>
    public static IReadOnlyList<DialChannel> All => AllChannels;

    /// <summary>True for a defined channel.</summary>
    public static bool IsValid(DialChannel channel) => (uint)channel < Count;

    /// <summary>Stable JSON id ("TC1", "TC2", "TC3", "ABS", "BB"); empty for an invalid value.</summary>
    public static string Id(DialChannel channel) => IsValid(channel) ? Ids[(int)channel] : string.Empty;

    /// <summary>Parses a JSON id (case-insensitive, no trimming). Allocation-free.</summary>
    public static bool TryParseId(string id, out DialChannel channel)
    {
        if (id != null)
        {
            for (int i = 0; i < Count; i++)
            {
                if (string.Equals(id, Ids[i], StringComparison.OrdinalIgnoreCase))
                {
                    channel = (DialChannel)i;
                    return true;
                }
            }
        }

        channel = DialChannel.Tc1;
        return false;
    }

    /// <summary>UI name ("TC", "TC2 (Cut)", "TC3 (Slip)", "ABS", "Brake bias").</summary>
    public static string DisplayName(DialChannel channel) => IsValid(channel) ? DisplayNames[(int)channel] : string.Empty;

    /// <summary>Compact UI label for value chips ("TC", "TC2", "TC3", "ABS", "BB").</summary>
    public static string ShortName(DialChannel channel) => IsValid(channel) ? ShortNames[(int)channel] : string.Empty;

    /// <summary>Integer steps or continuous.</summary>
    public static DialChannelKind Kind(DialChannel channel) => IsValid(channel) ? Kinds[(int)channel] : DialChannelKind.Integer;

    /// <summary>.NET format string for UI values ("0" or "0.0 '%'").</summary>
    public static string DisplayFormat(DialChannel channel) => IsValid(channel) ? Formats[(int)channel] : IntegerFormat;

    /// <summary>
    /// Default comparison tolerance: <see cref="IntegerTolerance"/> for integer channels (meaning "equal after
    /// rounding", see <see cref="Matches"/>), <see cref="BrakeBiasTolerance"/> for brake bias.
    /// </summary>
    public static double DefaultTolerance(DialChannel channel) => IsValid(channel) ? Tolerances[(int)channel] : IntegerTolerance;

    /// <summary>Lowest valid value (0 for every current channel).</summary>
    public static double MinValue(DialChannel channel) => 0.0;

    /// <summary>Highest sane value (independent of the car; the car's real maximum comes from telemetry).</summary>
    public static double MaxValue(DialChannel channel) => IsValid(channel) ? MaxValues[(int)channel] : MaxIntegerValue;

    /// <summary>Default Control Mapper role that the dialer presses to increase the value.</summary>
    public static string DefaultIncreaseRole(DialChannel channel) => IsValid(channel) ? IncreaseRoles[(int)channel] : string.Empty;

    /// <summary>Default Control Mapper role that the dialer presses to decrease the value.</summary>
    public static string DefaultDecreaseRole(DialChannel channel) => IsValid(channel) ? DecreaseRoles[(int)channel] : string.Empty;

    /// <summary>
    /// Repairs a stored/entered value: non-finite gives NaN; otherwise clamped to [<see cref="MinValue"/>,
    /// <see cref="MaxValue"/>], integer channels rounded to whole numbers, continuous ones to
    /// <see cref="ContinuousDecimals"/> decimals. Allocation-free.
    /// </summary>
    public static double Sanitize(DialChannel channel, double value)
    {
        if (!MathUtil.IsFinite(value))
        {
            return double.NaN;
        }

        double clamped = MathUtil.Clamp(value, MinValue(channel), MaxValue(channel));
        return Kind(channel) == DialChannelKind.Integer
            ? Math.Round(clamped, MidpointRounding.AwayFromZero)
            : Math.Round(clamped, ContinuousDecimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The single "target reached" rule shared by the dialer and the module ("already set" checks). Integer channels:
    /// equal after rounding. Continuous: |current − target| ≤ max(<see cref="DefaultTolerance"/>, learnedStep / 2)
    /// (a non-positive or non-finite <paramref name="learnedStep"/> is ignored). False when either value is not
    /// finite. Allocation-free.
    /// </summary>
    public static bool Matches(DialChannel channel, double current, double target, double learnedStep)
    {
        if (!MathUtil.IsFinite(current) || !MathUtil.IsFinite(target))
        {
            return false;
        }

        if (Kind(channel) == DialChannelKind.Integer)
        {
            return Math.Round(current, MidpointRounding.AwayFromZero) == Math.Round(target, MidpointRounding.AwayFromZero);
        }

        double tolerance = DefaultTolerance(channel);
        if (MathUtil.IsFinite(learnedStep) && learnedStep > 0.0)
        {
            tolerance = Math.Max(tolerance, learnedStep * 0.5);
        }

        return Math.Abs(current - target) <= tolerance;
    }

    /// <summary>Formats a value for the UI with <see cref="DisplayFormat"/> (invariant culture); <see cref="MissingValueText"/> when not finite. Allocates: UI only.</summary>
    public static string FormatValue(DialChannel channel, double value) =>
        MathUtil.IsFinite(value) ? value.ToString(DisplayFormat(channel), CultureInfo.InvariantCulture) : MissingValueText;
}
