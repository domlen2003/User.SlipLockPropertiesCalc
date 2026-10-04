using System;

namespace DivebombLogistics.Core;

/// <summary>
/// Small allocation-free math helpers. .NET Framework 4.8 lacks <c>Math.Clamp</c> and
/// <c>double.IsFinite</c>, so they live here.
/// </summary>
internal static class MathUtil
{
    /// <summary>Standard gravity in m/s².</summary>
    public const double Gravity = 9.80665;

    public const double DegToRad = Math.PI / 180.0;
    public const double RadToDeg = 180.0 / Math.PI;

    public static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    public static double Clamp01(double value) => Clamp(value, 0.0, 1.0);

    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>Returns <paramref name="value"/> if finite, otherwise <paramref name="fallback"/>.</summary>
    public static double FiniteOr(double value, double fallback) => IsFinite(value) ? value : fallback;

    /// <summary>Sign as -1, 0 or +1 (NaN maps to 0).</summary>
    public static int Sign(double value)
    {
        if (value > 0)
        {
            return 1;
        }

        return value < 0 ? -1 : 0;
    }

    /// <summary>
    /// Piecewise-linear intensity mapping: <paramref name="x"/> at or below <paramref name="onset"/> maps to 0,
    /// at or above <paramref name="full"/> maps to 1, linear in between, then raised to <paramref name="gamma"/>.
    /// Non-finite input maps to 0. If <paramref name="full"/> is not above <paramref name="onset"/>, acts as a step.
    /// </summary>
    public static double Map(double x, double onset, double full, double gamma = 1.0)
    {
        if (!IsFinite(x))
        {
            return 0.0;
        }

        if (!(full > onset))
        {
            return x > onset ? 1.0 : 0.0;
        }

        double t = Clamp01((x - onset) / (full - onset));
        if (gamma == 1.0 || t <= 0.0 || t >= 1.0)
        {
            return t;
        }

        return Math.Pow(t, gamma);
    }

    /// <summary>
    /// Mean of a 4-element array, summed strictly left to right ((a0+a1)+a2)+a3 so results are
    /// bit-identical with the legacy implementation.
    /// </summary>
    public static double Average4(double[] values) => (values[0] + values[1] + values[2] + values[3]) / 4;

    /// <summary>First-order smoothing factor for a time constant: dt / (tau + dt). Returns 1 when tau &lt;= 0.</summary>
    public static double LagAlpha(double dt, double tau) => tau <= 0.0 ? 1.0 : dt / (tau + dt);
}
