using System;
using System.Text;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>
/// Reads one dial channel of one sim: the value from the first of one or more candidate property paths (each with
/// its own unit conversion) and, optionally, the car's maximum from a separate path.
/// </summary>
/// <remarks>
/// <para>
/// Every path is a full string fixed at construction, so reads never concatenate and never allocate. Once a
/// candidate produced a value it stays selected for the lifetime of this object (a new instance is created on every
/// game change), so a fallback path is never preferred over the primary one after the primary was seen.
/// </para>
/// <para>
/// While unresolved, the primary candidate is read on every call (that lookup is needed anyway), but the fallback
/// candidates only every <see cref="FallbackProbeInterval"/> calls: a lookup of a missing raw-data path is
/// comparatively expensive in SimHub and the module reads every channel every frame.
/// </para>
/// <para>
/// Validity: integer channels need a finite whole-number value ≥ <see cref="DialChannels.MinValue"/> (AC EVO
/// reports -1 for controls a car lacks; a fractional reading means the path holds another quantity); brake bias needs a finite front share strictly between 0 and 100 %, because sims report 0
/// (SimHub <c>GameData.BrakeBias</c>, zeroed shared memory before a car is loaded) when there is no real value.
/// </para>
/// </remarks>
internal sealed class DialChannelSource
{
    /// <summary>Calls between two probes of the fallback candidates while no candidate has been found.</summary>
    internal const int FallbackProbeInterval = 30;

    private const double PercentPerFraction = 100.0;

    /// <summary>AC EVO fallback: a front share below this is a fraction, otherwise percent.</summary>
    private const double FractionUpperBound = 1.0;

    /// <summary>Largest distance from a whole number an integer-channel reading may have (float-to-double noise).</summary>
    private const double IntegerTolerance = 1e-4;

    private const string StatePresent = "present";
    private const string StateMissing = "missing";
    private const string StateInvalid = "no valid value";
    private const string StateNotRead = "not read yet";

    private readonly string[] paths;
    private readonly DialValueTransform[] transforms;
    private readonly string maxPath;
    private readonly bool isBrakeBias;

    private int resolvedIndex = -1;
    private int fallbackCountdown;

    /// <summary>
    /// Creates a channel reader. <paramref name="paths"/> and <paramref name="transforms"/> are parallel arrays in
    /// order of preference (at least one entry, no null path). <paramref name="maxPath"/> may be null (no maximum).
    /// </summary>
    public DialChannelSource(DialChannel channel, string[] paths, DialValueTransform[] transforms, string maxPath)
    {
        if (paths == null || paths.Length == 0)
        {
            throw new ArgumentException("At least one property path is required.", nameof(paths));
        }

        if (transforms == null || transforms.Length != paths.Length)
        {
            throw new ArgumentException("One transform per path is required.", nameof(transforms));
        }

        for (int i = 0; i < paths.Length; i++)
        {
            if (paths[i] == null)
            {
                throw new ArgumentException("Property paths must not be null.", nameof(paths));
            }
        }

        Channel = channel;
        this.paths = paths;
        this.transforms = transforms;
        this.maxPath = maxPath;
        isBrakeBias = DialChannels.Kind(channel) == DialChannelKind.Continuous;
    }

    /// <summary>The channel this source reads.</summary>
    public DialChannel Channel { get; }

    /// <summary>Result of the most recent value lookup.</summary>
    public DialPathState ValueState { get; private set; }

    /// <summary>Result of the most recent maximum lookup (<see cref="DialPathState.None"/> without a maximum path).</summary>
    public DialPathState MaxState { get; private set; }

    /// <summary>The candidate path that produced a value, or null while unresolved.</summary>
    public string ResolvedPath => resolvedIndex >= 0 ? paths[resolvedIndex] : null;

    /// <summary>The maximum path, or null when the sim reports no maximum for this channel.</summary>
    public string MaxPath => maxPath;

    /// <summary>Single-path convenience factory.</summary>
    public static DialChannelSource Single(DialChannel channel, string path, DialValueTransform transform, string maxPath) =>
        new DialChannelSource(channel, new[] { path }, new[] { transform }, maxPath);

    /// <summary>Two-candidate factory (primary, fallback).</summary>
    public static DialChannelSource WithFallback(
        DialChannel channel,
        string path,
        DialValueTransform transform,
        string fallbackPath,
        DialValueTransform fallbackTransform,
        string maxPath) =>
        new DialChannelSource(channel, new[] { path, fallbackPath }, new[] { transform, fallbackTransform }, maxPath);

    /// <summary>Reads and converts the current value. False and NaN when missing or invalid. Allocation-free, never throws.</summary>
    public bool TryRead(ITelemetryReader reader, out double value)
    {
        double raw;
        int index = resolvedIndex;
        bool found = index >= 0 ? reader.TryGetDouble(paths[index], out raw) : Probe(reader, out raw, out index);
        if (!found)
        {
            ValueState = DialPathState.Missing;
            value = double.NaN;
            return false;
        }

        value = Convert(transforms[index], raw);
        if (!IsUsable(value))
        {
            ValueState = DialPathState.Invalid;
            value = double.NaN;
            return false;
        }

        ValueState = DialPathState.Present;
        return true;
    }

    /// <summary>Reads the car's maximum. False and NaN without a maximum path, when missing, non-finite or ≤ 0.</summary>
    public bool TryGetMax(ITelemetryReader reader, out double max)
    {
        if (maxPath == null)
        {
            MaxState = DialPathState.None;
            max = double.NaN;
            return false;
        }

        if (!reader.TryGetDouble(maxPath, out max))
        {
            MaxState = DialPathState.Missing;
            max = double.NaN;
            return false;
        }

        if (!MathUtil.IsFinite(max) || max <= 0.0)
        {
            MaxState = DialPathState.Invalid;
            max = double.NaN;
            return false;
        }

        MaxState = DialPathState.Present;
        return true;
    }

    /// <summary>
    /// A number that changes whenever <see cref="Describe"/> would print something different (resolution, value
    /// state, maximum state). Allocation-free.
    /// </summary>
    public int Signature() => ((resolvedIndex + 1) * 100) + ((int)ValueState * 10) + (int)MaxState;

    /// <summary>Appends one diagnostics line (allocates; diagnostics only).</summary>
    public void Describe(StringBuilder builder)
    {
        builder.Append("  ").Append(DialChannels.DisplayName(Channel)).Append(": ");
        if (resolvedIndex >= 0)
        {
            builder.Append(paths[resolvedIndex]).Append(" (").Append(StateText(ValueState)).Append(')');
        }
        else
        {
            builder.Append(paths.Length == 1 ? paths[0] : string.Join(" | ", paths))
                .Append(" (")
                .Append(ValueState == DialPathState.None ? StateNotRead : StateMissing)
                .Append(')');
        }

        if (maxPath != null)
        {
            builder.Append("; max ").Append(maxPath).Append(" (").Append(StateText(MaxState)).Append(')');
        }

        builder.AppendLine();
    }

    private static string StateText(DialPathState state)
    {
        switch (state)
        {
            case DialPathState.Present:
                return StatePresent;
            case DialPathState.Missing:
                return StateMissing;
            case DialPathState.Invalid:
                return StateInvalid;
            default:
                return StateNotRead;
        }
    }

    private static double Convert(DialValueTransform transform, double raw)
    {
        switch (transform)
        {
            case DialValueTransform.FrontFractionToPercent:
                return raw * PercentPerFraction;
            case DialValueTransform.RearFractionToFrontPercent:
                return (1.0 - raw) * PercentPerFraction;
            case DialValueTransform.FractionOrPercent:
                return raw < FractionUpperBound ? raw * PercentPerFraction : raw;
            default:
                return raw;
        }
    }

    private bool IsUsable(double value)
    {
        if (!MathUtil.IsFinite(value))
        {
            return false;
        }

        if (isBrakeBias)
        {
            return value > DialChannels.MinValue(Channel) && value < DialChannels.MaxValue(Channel);
        }

        // Integer levels must actually be whole numbers. A fractional reading means the path holds a different
        // quantity (e.g. AC EVO Physics.tc is a float, not the TC level); dialing it would round to a wrong level and
        // press towards the limit, so it is reported as "no valid value" instead.
        return value >= DialChannels.MinValue(Channel) && Math.Abs(value - Math.Round(value)) <= IntegerTolerance;
    }

    private bool Probe(ITelemetryReader reader, out double raw, out int index)
    {
        if (reader.TryGetDouble(paths[0], out raw))
        {
            resolvedIndex = index = 0;
            return true;
        }

        index = -1;
        if (paths.Length == 1)
        {
            return false;
        }

        if (fallbackCountdown > 0)
        {
            fallbackCountdown--;
            return false;
        }

        fallbackCountdown = FallbackProbeInterval - 1;
        for (int i = 1; i < paths.Length; i++)
        {
            if (reader.TryGetDouble(paths[i], out raw))
            {
                resolvedIndex = index = i;
                return true;
            }
        }

        raw = double.NaN;
        return false;
    }
}
