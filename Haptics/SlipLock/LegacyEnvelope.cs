using System;

namespace DivebombLogistics.Haptics.SlipLock;

/// <summary>
/// Attack/release envelope of the v1 post-processor, ported operation for operation so exported values stay
/// bit-identical with v1 (the equivalence tests compare with <c>==</c>; do not "simplify" the arithmetic).
/// Works on the 0..100 percent scale used by every slip/lock channel.
/// </summary>
internal static class LegacyEnvelope
{
    /// <summary>Time constants below this (ms) mean "no smoothing": the state jumps straight to the target.</summary>
    private const double MinTimeConstantMs = 1.0;

    /// <summary>Lower bound of the release level factor, so the release time never collapses to zero.</summary>
    private const double MinReleaseLevel = 0.01;

    /// <summary>States below this are snapped to 0 to avoid a long tail of float dust.</summary>
    private const double SnapToZeroBelow = 0.001;

    private const double FullScale = 100.0;
    private const double MsPerSecond = 1000.0;

    /// <summary>
    /// Advances <paramref name="state"/> one step towards <paramref name="target"/> and returns the new output.
    /// Rising values use an exponential attack with <paramref name="attackMs"/>. Falling values use a release
    /// time scaled by sqrt(level): high levels linger, low levels drop out quickly (concave release).
    /// </summary>
    /// <param name="state">Envelope state (persisted by the caller between frames).</param>
    /// <param name="target">Target value (0..100).</param>
    /// <param name="dt">Frame time in seconds (the caller clamps it).</param>
    /// <param name="attackMs">Attack time constant in milliseconds.</param>
    /// <param name="releaseMs">Release time constant at full scale in milliseconds.</param>
    /// <returns>The envelope output, clamped to 0..100.</returns>
    public static double Apply(ref double state, double target, double dt, double attackMs, double releaseMs)
    {
        if (target >= state)
        {
            if (attackMs < MinTimeConstantMs)
            {
                state = target;
            }
            else
            {
                double rate = 1.0 - Math.Exp(-dt / (attackMs / MsPerSecond));
                state += (target - state) * rate;
            }
        }
        else
        {
            double level = Math.Max(MinReleaseLevel, state / FullScale);
            double effectiveMs = releaseMs * Math.Sqrt(level);
            if (effectiveMs < MinTimeConstantMs)
            {
                state = target;
            }
            else
            {
                double rate = 1.0 - Math.Exp(-dt / (effectiveMs / MsPerSecond));
                state += (target - state) * rate;
            }
        }

        if (state < SnapToZeroBelow)
        {
            state = 0;
        }

        return Math.Max(0, Math.Min(FullScale, state));
    }
}
