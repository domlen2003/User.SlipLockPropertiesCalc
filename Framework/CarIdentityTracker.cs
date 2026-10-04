using System;
using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Framework;

/// <summary>
/// Shared car identity resolution of the shell. Resolves <see cref="CarIdentityResolver"/> again whenever SimHub's
/// <c>CarId</c>/<c>CarModel</c> change, and, while the key is a livery-specific fallback on a sim that normally
/// reports a native model name (LMU's first frames), retries every <see cref="RetryIntervalSeconds"/> for at most
/// <see cref="RetryWindowSeconds"/> after the car appeared.
/// <para>Data thread only (except <see cref="Current"/>). Allocation-free unless the car changes or a retry is due.</para>
/// </summary>
internal sealed class CarIdentityTracker : ICarIdentityState
{
    /// <summary>LMU's native model name can be empty for the first frames: retry resolution this often ...</summary>
    public const double RetryIntervalSeconds = 1.0;

    /// <summary>... for at most this long after the car appeared.</summary>
    public const double RetryWindowSeconds = 10.0;

    private volatile CarIdentity current = CarIdentity.None;
    private bool dirty = true;
    private string seenCarId;
    private string seenCarModel;
    private double retryUntil = double.NegativeInfinity;
    private double nextRetry = double.PositiveInfinity;

    /// <inheritdoc />
    public CarIdentity Current => current;

    /// <inheritdoc />
    public double ProvisionalUntil => retryUntil;

    /// <inheritdoc />
    public bool IsProvisional(double now) => current.ShouldRetry && now <= retryUntil;

    /// <summary>The game changed: forget the car (<see cref="Current"/> becomes None) and resolve again on the next <see cref="Update"/>.</summary>
    public void Reset()
    {
        current = CarIdentity.None;
        dirty = true;
    }

    /// <summary>
    /// Resolves the identity when the car changed or a retry is due. Returns true when <see cref="Current"/> now
    /// denotes another car (the caller notifies the modules).
    /// </summary>
    public bool Update(FrameContext frame, ITelemetryReader reader, double now)
    {
        bool changed = dirty
            || !string.Equals(frame.CarId, seenCarId, StringComparison.Ordinal)
            || !string.Equals(frame.CarModel, seenCarModel, StringComparison.Ordinal);
        bool retry = !changed && current.ShouldRetry && now >= nextRetry && now <= retryUntil;
        if (!changed && !retry)
        {
            return false;
        }

        // Rare path (car change or bounded retry): allocations are fine here.
        dirty = false;
        seenCarId = frame.CarId;
        seenCarModel = frame.CarModel;
        CarIdentity resolved = CarIdentityResolver.Resolve(frame, reader);
        if (changed)
        {
            retryUntil = now + RetryWindowSeconds;
        }

        nextRetry = resolved.ShouldRetry ? now + RetryIntervalSeconds : double.PositiveInfinity;
        if (resolved.HasCar == current.HasCar && resolved.SameCarAs(current))
        {
            return false;
        }

        current = resolved;
        return true;
    }
}
