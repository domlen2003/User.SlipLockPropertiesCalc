using System;
using DivebombLogistics.Core;

namespace DivebombLogistics.Haptics.Balance;

/// <summary>
/// First-order exponential moving average. The first finite sample initializes the value directly, so there is no
/// start-up transient from zero. Non-finite samples are ignored so a single NaN cannot poison the state.
/// </summary>
internal sealed class Ema
{
    /// <summary>Current filtered value (NaN until the first finite sample).</summary>
    public double Value { get; private set; } = double.NaN;

    /// <summary>True once a finite sample was received since construction or <see cref="Reset"/>.</summary>
    public bool HasValue { get; private set; }

    /// <summary>Feeds one sample and returns the filtered value.</summary>
    /// <param name="x">Sample; non-finite values leave the state unchanged.</param>
    /// <param name="alpha">Smoothing factor 0..1 (1 = no smoothing), usually <see cref="MathUtil.LagAlpha"/>.</param>
    public double Update(double x, double alpha)
    {
        if (!MathUtil.IsFinite(x))
        {
            return Value;
        }

        if (!HasValue)
        {
            Value = x;
            HasValue = true;
            return x;
        }

        Value += (x - Value) * MathUtil.Clamp01(alpha);
        return Value;
    }

    public void Reset()
    {
        Value = double.NaN;
        HasValue = false;
    }
}

/// <summary>
/// Running median of the last three samples. Rejects single-sample spikes (kerb strikes on the yaw rate) at the
/// cost of exactly one sample of delay on monotonic signals. The first sample fills the whole window, so a spike
/// right after start-up is rejected too. Non-finite samples are ignored.
/// </summary>
internal sealed class Median3
{
    private double oldest;
    private double middle;
    private double newest;

    /// <summary>True once a finite sample was received since construction or <see cref="Reset"/>.</summary>
    public bool HasValue { get; private set; }

    /// <summary>Feeds one sample and returns the median of the window (NaN while empty).</summary>
    public double Update(double x)
    {
        if (MathUtil.IsFinite(x))
        {
            if (HasValue)
            {
                oldest = middle;
                middle = newest;
                newest = x;
            }
            else
            {
                oldest = x;
                middle = x;
                newest = x;
                HasValue = true;
            }
        }

        return HasValue ? MedianOf(oldest, middle, newest) : double.NaN;
    }

    public void Reset() => HasValue = false;

    private static double MedianOf(double a, double b, double c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
}

/// <summary>
/// Asymmetric exponential envelope for a 0..1 intensity: moves toward the target with the attack time constant
/// when rising and with the release time constant when falling, independent of the sample rate. Within
/// <see cref="SnapDistance"/> of the target it snaps onto it, so an idle output reads exactly 0.
/// </summary>
internal sealed class AttackRelease
{
    /// <summary>Closer than this to the target the envelope jumps onto it (far below anything a haptic device renders).</summary>
    public const double SnapDistance = 1e-4;

    /// <summary>Current envelope value.</summary>
    public double Value { get; private set; }

    /// <summary>Advances the envelope by <paramref name="dt"/> seconds toward <paramref name="target"/>.</summary>
    /// <param name="target">Target intensity; non-finite is treated as 0.</param>
    /// <param name="dt">Elapsed time in seconds; values &lt;= 0 leave the envelope unchanged.</param>
    /// <param name="attack">Rise time constant in seconds (&lt;= 0 = instant).</param>
    /// <param name="release">Fall time constant in seconds (&lt;= 0 = instant).</param>
    public double Update(double target, double dt, double attack, double release)
    {
        if (!MathUtil.IsFinite(target))
        {
            target = 0.0;
        }

        if (!(dt > 0.0))
        {
            return Value;
        }

        double tau = target > Value ? attack : release;
        double alpha = tau > 0.0 ? 1.0 - Math.Exp(-dt / tau) : 1.0;
        Value += (target - Value) * alpha;
        if (Math.Abs(target - Value) < SnapDistance)
        {
            Value = target;
        }

        return Value;
    }

    public void Reset() => Value = 0.0;
}

/// <summary>
/// Onset hysteresis for a 0..1 intensity: the output stays 0 until the raw intensity reaches <c>armAt</c>, then
/// follows the raw intensity until it falls back to 0 (the detector metric returned to its onset). This stops a
/// metric that hovers around its onset from chattering the haptic output on and off.
/// </summary>
internal sealed class HysteresisGate
{
    /// <summary>True while the gate passes the raw intensity through.</summary>
    public bool Armed { get; private set; }

    /// <summary>Applies the hysteresis and returns the gated intensity.</summary>
    /// <param name="raw">Raw intensity (&lt;= 0 or NaN disarms).</param>
    /// <param name="armAt">Raw intensity at which the gate arms (&lt;= 0 arms on any positive value).</param>
    public double Apply(double raw, double armAt)
    {
        if (!(raw > 0.0))
        {
            Armed = false;
            return 0.0;
        }

        if (!Armed && raw >= armAt)
        {
            Armed = true;
        }

        return Armed ? raw : 0.0;
    }

    public void Reset() => Armed = false;
}

/// <summary>Outcome of a <see cref="SignVote"/>.</summary>
internal enum SignVerdict
{
    /// <summary>Not enough (or only ambiguous) evidence yet.</summary>
    Pending = 0,

    /// <summary>The convention in use is correct.</summary>
    Confirmed,

    /// <summary>The convention in use is inverted.</summary>
    Inverted,
}

/// <summary>
/// Majority vote over the sign of a quantity, used to verify sign conventions at runtime (steering vs. yaw rate,
/// forward velocity). After the required number of votes it decides only on clear evidence; an ambiguous result
/// restarts the vote instead of confirming a possibly wrong convention.
/// </summary>
internal sealed class SignVote
{
    private readonly int requiredVotes;
    private readonly double decisionThreshold;
    private int sum;

    /// <param name="requiredVotes">Votes collected before deciding (&gt;= 1).</param>
    /// <param name="decisionThreshold">|mean vote| needed to decide, 0..1 (0.5 = at least 75 % agreement).</param>
    public SignVote(int requiredVotes, double decisionThreshold)
    {
        this.requiredVotes = Math.Max(1, requiredVotes);
        this.decisionThreshold = MathUtil.Clamp01(decisionThreshold);
    }

    /// <summary>Votes collected since the last decision or reset.</summary>
    public int Count { get; private set; }

    /// <summary>Adds a vote (&gt; 0 agrees with the current convention, &lt; 0 disagrees, 0 is ignored).</summary>
    /// <returns>The verdict once enough votes were collected (the vote then restarts), else <see cref="SignVerdict.Pending"/>.</returns>
    public SignVerdict Add(int sign)
    {
        if (sign == 0)
        {
            return SignVerdict.Pending;
        }

        sum += sign > 0 ? 1 : -1;
        Count++;
        if (Count < requiredVotes)
        {
            return SignVerdict.Pending;
        }

        double mean = (double)sum / Count;
        Reset();
        if (mean > decisionThreshold)
        {
            return SignVerdict.Confirmed;
        }

        return mean < -decisionThreshold ? SignVerdict.Inverted : SignVerdict.Pending;
    }

    public void Reset()
    {
        sum = 0;
        Count = 0;
    }
}
