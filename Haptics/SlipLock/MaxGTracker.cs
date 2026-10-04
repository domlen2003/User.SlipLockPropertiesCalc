using System;

namespace DivebombLogistics.Haptics.SlipLock;

/// <summary>
/// Tracks the maximum lateral/longitudinal acceleration seen for the current car (used to normalize corner load).
/// Exact v1 semantics: starts at 5, only grows, and rejects jumps of 5 or more in one frame (spike filter).
/// </summary>
internal sealed class MaxGTracker
{
    public const double Initial = 5.0;
    private const double MaxJump = 5.0;

    public double MaxSway { get; private set; } = Initial;

    public double MaxSurge { get; private set; } = Initial;

    public double MaxDecel { get; private set; } = Initial;

    public void Reset()
    {
        MaxSway = Initial;
        MaxSurge = Initial;
        MaxDecel = Initial;
    }

    /// <param name="sway">SimHub AccelerationSway.</param>
    /// <param name="surge">SimHub AccelerationSurge.</param>
    public void Update(double sway, double surge)
    {
        double s = Math.Abs(sway);
        if (s > MaxSway && s < MaxSway + MaxJump)
        {
            MaxSway = s;
        }

        if (surge > MaxSurge && surge < MaxSurge + MaxJump)
        {
            MaxSurge = surge;
        }

        double decel = -surge;
        if (decel > MaxDecel && decel < MaxDecel + MaxJump)
        {
            MaxDecel = decel;
        }
    }
}
