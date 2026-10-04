using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DivebombLogistics.Core;
using DivebombLogistics.SpeedDial.Engine;

namespace DivebombLogistics.SpeedDial.UI;

/// <summary>
/// Texts and number formatting of the Speed Dial tab. Enum texts are constants (no allocation per refresh); numbers use
/// the invariant culture like the rest of the DLP UI (SimHub's number boxes format with en-US).
/// </summary>
internal static class SpeedDialText
{
    /// <summary>Shown for unknown values.</summary>
    public const string Missing = DialChannels.MissingValueText;

    /// <summary>Separator between the parts of a one-line summary ("TC 3 · ABS 5").</summary>
    public const string Separator = "  ·  ";

    /// <summary>Culture for every formatted or parsed number in the tab.</summary>
    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Edit format of integer channels (TC, ABS): whole numbers without unit.</summary>
    private const string IntegerEditFormat = "0";

    /// <summary>Edit format of continuous channels (brake bias): up to <see cref="DialChannels.ContinuousDecimals"/> decimals, no unit.</summary>
    private const string ContinuousEditFormat = "0.0#";

    private const string PercentSign = "%";
    private const string TimeFormat = "HH:mm:ss";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>Text of a dial job state.</summary>
    public static string State(DialState state) => state switch
    {
        DialState.Idle => "Idle",
        DialState.Running => "Dialing",
        DialState.Paused => "Paused (game paused, in a menu or replay)",
        DialState.Completed => "Completed",
        DialState.Partial => "Partly done",
        DialState.Failed => "Failed",
        DialState.Cancelled => "Cancelled",
        _ => Missing,
    };

    /// <summary>Text of a per-channel dial result.</summary>
    public static string Result(ChannelResult result) => result switch
    {
        ChannelResult.Pending => "waiting",
        ChannelResult.Reached => "reached",
        ChannelResult.ClosestPossible => "closest possible",
        ChannelResult.NoTelemetry => "no telemetry",
        ChannelResult.NoBinding => "no role / Control Mapper",
        ChannelResult.NoResponse => "no response",
        ChannelResult.LimitReached => "limit reached",
        ChannelResult.DirectionError => "direction error",
        ChannelResult.MaxPresses => "too many presses",
        ChannelResult.Cancelled => "cancelled",
        ChannelResult.Skipped => "skipped",
        _ => Missing,
    };

    /// <summary>A value for an edit box: number without unit, empty when unknown.</summary>
    public static string EditText(DialChannel channel, double value)
    {
        if (!MathUtil.IsFinite(value))
        {
            return string.Empty;
        }

        string format = DialChannels.Kind(channel) == DialChannelKind.Integer ? IntegerEditFormat : ContinuousEditFormat;
        return value.ToString(format, Culture);
    }

    /// <summary>
    /// Parses a typed value: surrounding spaces and a percent sign are ignored, a decimal comma is accepted.
    /// False for empty, invalid or non-finite input.
    /// </summary>
    public static bool TryParseValue(string text, out double value)
    {
        value = double.NaN;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string cleaned = text.Replace(PercentSign, string.Empty).Replace(',', '.').Trim();
        return double.TryParse(cleaned, NumberStyles.Float, Culture, out value) && MathUtil.IsFinite(value);
    }

    /// <summary>"TC · TC2 · BB" for a list of channels; <paramref name="empty"/> when there are none.</summary>
    public static string ChannelList(IReadOnlyList<DialChannel> channels, string empty)
    {
        if (channels == null || channels.Count == 0)
        {
            return empty;
        }

        var text = new StringBuilder();
        for (int i = 0; i < channels.Count; i++)
        {
            if (i > 0)
            {
                text.Append(Separator);
            }

            text.Append(DialChannels.ShortName(channels[i]));
        }

        return text.ToString();
    }

    /// <summary>"TC 3" (short name and formatted value, "-" when unknown).</summary>
    public static string Chip(DialChannel channel, double value) =>
        DialChannels.ShortName(channel) + " " + DialChannels.FormatValue(channel, value);

    /// <summary>A UTC time stamp in local time: only the time when it is today, else date and time; empty for null.</summary>
    public static string LocalTime(DateTime? utc)
    {
        if (!utc.HasValue)
        {
            return string.Empty;
        }

        DateTime local = utc.Value.Kind == DateTimeKind.Local ? utc.Value : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime();
        return local.Date == DateTime.Now.Date ? local.ToString(TimeFormat, Culture) : local.ToString(DateTimeFormat, Culture);
    }
}
