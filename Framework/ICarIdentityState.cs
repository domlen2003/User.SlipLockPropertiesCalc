using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Framework;

/// <summary>
/// The player's car as resolved by the shell (shared by all modules), plus the LMU retry window during which a
/// livery-specific placeholder key may still be replaced by the native model name.
/// </summary>
internal interface ICarIdentityState
{
    /// <summary>The current identity (<see cref="CarIdentity.None"/> without a car). Any thread.</summary>
    CarIdentity Current { get; }

    /// <summary>
    /// End of the retry window (monotonic seconds) of the latest car change. Data thread only. The shell starts the
    /// new car's window before <see cref="IDlpModule.OnCarChanged"/> runs, so a module that keeps its own copy of the
    /// identity (to know the previous car there) must capture this value together with that copy in OnCarChanged and
    /// judge the copy as <c>identity.ShouldRetry &amp;&amp; now &lt;= capturedProvisionalUntil</c>; it must never combine
    /// its previous identity with the shell's current window.
    /// </summary>
    double ProvisionalUntil { get; }

    /// <summary>
    /// True while <see cref="Current"/> is a placeholder (<see cref="CarIdentity.ShouldRetry"/> inside the retry
    /// window): per-car data must not be saved under its key. Data thread only.
    /// </summary>
    bool IsProvisional(double now);
}
