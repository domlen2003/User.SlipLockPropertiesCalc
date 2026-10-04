using System;
using DivebombLogistics.Core;

namespace DivebombLogistics.SpeedDial.Model;

/// <summary>
/// What the dialer learned about one channel of one car (<see cref="SpeedDialCarData.Learning"/>): which way the
/// Increase role moves the value, the smallest step seen, and the observed value range. Updated by the dialer on the
/// data thread (allocation-free methods); persisted with the car data. Plain JSON DTO.
/// <para>Unknown values: <see cref="Step"/> 0, <see cref="ObservedMin"/>/<see cref="ObservedMax"/> NaN (written as the
/// JSON string "NaN" by <c>JsonFile</c>).</para>
/// </summary>
public sealed class ChannelLearning
{
    /// <summary><see cref="Direction"/>: the Increase role raises the value (default assumption).</summary>
    public const int IncreaseRaises = 1;

    /// <summary><see cref="Direction"/>: the Increase role lowers the value (e.g. a game that counts TC the other way).</summary>
    public const int IncreaseLowers = -1;

    /// <summary>Changes smaller than this are float noise, not a step.</summary>
    public const double MinStep = 1e-6;

    /// <summary>Effect of the Increase role on the value: <see cref="IncreaseRaises"/> or <see cref="IncreaseLowers"/>.</summary>
    public int Direction = IncreaseRaises;

    /// <summary>True once a press moved the value the way <see cref="Direction"/> predicts (or after the one allowed flip).</summary>
    public bool DirectionConfirmed;

    /// <summary>Smallest |Δ| observed after one press; 0 = unknown.</summary>
    public double Step;

    /// <summary>Lowest value seen while dialing; NaN = unknown.</summary>
    public double ObservedMin = double.NaN;

    /// <summary>Highest value seen while dialing; NaN = unknown.</summary>
    public double ObservedMax = double.NaN;

    /// <summary>True when <see cref="Step"/> is known.</summary>
    public bool HasStep() => MathUtil.IsFinite(Step) && Step > 0.0;

    /// <summary>Widens the observed range by <paramref name="value"/> (ignored when not finite). Returns true when it changed. Allocation-free.</summary>
    public bool Observe(double value)
    {
        if (!MathUtil.IsFinite(value))
        {
            return false;
        }

        bool changed = false;
        if (!(value >= ObservedMin))
        {
            ObservedMin = value;
            changed = true;
        }

        if (!(value <= ObservedMax))
        {
            ObservedMax = value;
            changed = true;
        }

        return changed;
    }

    /// <summary>Records the |Δ| of one press; keeps the smallest. Returns true when <see cref="Step"/> changed. Allocation-free.</summary>
    public bool RecordStep(double delta)
    {
        double magnitude = Math.Abs(delta);
        if (!MathUtil.IsFinite(magnitude) || magnitude < MinStep)
        {
            return false;
        }

        if (HasStep() && magnitude >= Step)
        {
            return false;
        }

        Step = magnitude;
        return true;
    }

    /// <summary>Forgets everything (UI "reset learning").</summary>
    public void Reset()
    {
        Direction = IncreaseRaises;
        DirectionConfirmed = false;
        Step = 0.0;
        ObservedMin = double.NaN;
        ObservedMax = double.NaN;
    }

    /// <summary>Repairs a deserialized instance: invalid direction → default (unconfirmed), invalid step → 0, swapped range fixed.</summary>
    public void Normalize()
    {
        if (Direction != IncreaseRaises && Direction != IncreaseLowers)
        {
            Direction = IncreaseRaises;
            DirectionConfirmed = false;
        }

        if (!HasStep())
        {
            Step = 0.0;
        }

        if (!MathUtil.IsFinite(ObservedMin))
        {
            ObservedMin = double.NaN;
        }

        if (!MathUtil.IsFinite(ObservedMax))
        {
            ObservedMax = double.NaN;
        }

        if (ObservedMin > ObservedMax)
        {
            double swap = ObservedMin;
            ObservedMin = ObservedMax;
            ObservedMax = swap;
        }
    }

    /// <summary>Copies every field into <paramref name="target"/> (allocation-free).</summary>
    public void CopyTo(ChannelLearning target)
    {
        target.Direction = Direction;
        target.DirectionConfirmed = DirectionConfirmed;
        target.Step = Step;
        target.ObservedMin = ObservedMin;
        target.ObservedMax = ObservedMax;
    }

    /// <summary>Independent copy.</summary>
    public ChannelLearning DeepCopy()
    {
        var copy = new ChannelLearning();
        CopyTo(copy);
        return copy;
    }
}
