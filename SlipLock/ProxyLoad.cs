using System;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.SlipLock;

/// <summary>
/// Corner-load estimate ("proxyL", v1 <c>CalcL</c>) from the car's lateral and longitudinal acceleration.
/// Result per wheel is 0..50 with 25 = neutral (static load); the slip/lock/ABS/TC channels scale by load/25 so a
/// loaded corner vibrates more than an unloaded one. The arithmetic is kept exactly as in v1 (bit-identical outputs).
/// </summary>
internal static class ProxyLoad
{
    /// <summary>Load of a wheel with no load transfer.</summary>
    public const double Neutral = 25.0;

    /// <summary>Upper bound of the load value (twice the neutral load).</summary>
    public const double Maximum = 50.0;

    /// <summary>Normalized acceleration is capped at 100 % of the car's maximum seen so far.</summary>
    private const double MaxPercent = 100.0;

    /// <summary>Guards the normalization against a zero maximum.</summary>
    private const double MinMaxAcceleration = 0.1;

    /// <summary>
    /// Computes the corner loads for one channel.
    /// </summary>
    /// <param name="sway">SimHub <c>AccelerationSway</c>; negative values load the left wheels.</param>
    /// <param name="surge">SimHub <c>AccelerationSurge</c>; positive (braking) loads the front wheels.</param>
    /// <param name="lateralInfluence">Lateral influence factor (preset percent / 100).</param>
    /// <param name="longitudinalInfluence">Longitudinal influence factor (preset percent / 100).</param>
    /// <param name="maxSway">Largest lateral acceleration seen for this car (normalization).</param>
    /// <param name="maxSurge">Largest longitudinal acceleration seen for this car (normalization; v1 uses it for both directions).</param>
    /// <param name="result">Receives the 4 corner loads (FL, FR, RL, RR), each 0..50.</param>
    public static void Calc(
        double sway,
        double surge,
        double lateralInfluence,
        double longitudinalInfluence,
        double maxSway,
        double maxSurge,
        double[] result)
    {
        // Percent of the car's maximum, weighted by the preset influence (v1 CalcL, same operation order).
        double lateral = Math.Min(MaxPercent, Math.Abs(sway) / Math.Max(maxSway, MinMaxAcceleration) * 100) * lateralInfluence;
        double longitudinal = Math.Min(MaxPercent, Math.Abs(surge) / Math.Max(maxSurge, MinMaxAcceleration) * 100) * longitudinalInfluence;

        for (int i = 0; i < Wheels.Count; i++)
        {
            // Wheels on the side the acceleration pushes towards gain load, the opposite side loses it.
            double lateralShare = (sway < 0) == (Wheels.LateralSign[i] < 0) ? lateral : -lateral;
            double longitudinalShare = (surge > 0) == (Wheels.LongitudinalSign[i] > 0) ? longitudinal : -longitudinal;

            double load = Neutral + Neutral * lateralShare / 100;
            load += load * longitudinalShare / 100;
            result[i] = Math.Max(0, Math.Min(Maximum, load));
        }
    }
}
