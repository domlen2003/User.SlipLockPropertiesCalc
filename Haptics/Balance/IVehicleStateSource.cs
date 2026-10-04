using DivebombLogistics.Core.Telemetry;

namespace DivebombLogistics.Haptics.Balance;

/// <summary>
/// Per-sim adapter that converts raw SimHub properties into a normalized <see cref="VehicleState"/>.
/// Implementations resolve and cache property paths, must be allocation-free per frame, and must never throw.
/// </summary>
internal interface IVehicleStateSource
{
    /// <summary>Short display name, e.g. "iRacing", "rFactor2/LMU", "ACC/AC", "Unsupported".</summary>
    string Name { get; }

    /// <summary>False for the null source (sim not supported): balance outputs stay 0.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Overwrites every field of <paramref name="state"/> for this frame (call <see cref="VehicleState.Clear"/> first
    /// or assign all fields). Sets <c>state.WallTime = ctx.WallTime</c>. Returns <c>state.Valid</c>.
    /// </summary>
    bool Read(FrameContext ctx, ITelemetryReader reader, VehicleState state);

    /// <summary>Forget cached paths/state (game change or retest).</summary>
    void Reset();

    /// <summary>
    /// Human-readable description of resolved property paths (for the diagnostics tab). May allocate;
    /// call only from the UI refresh path at low rate or on change.
    /// </summary>
    string DescribeResolution();
}
