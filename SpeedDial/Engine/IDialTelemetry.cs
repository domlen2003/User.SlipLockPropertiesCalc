namespace DivebombLogistics.SpeedDial.Engine;

/// <summary>
/// Reads the current dial channel values of one sim from SimHub properties (through <c>ITelemetryReader</c>).
/// Created per game by <c>DialTelemetryFactory.Create(gameName, reader)</c> on a game change; used on the data
/// thread only.
/// <para>
/// Units: integer channels as the sim's level number; <see cref="DialChannel.BrakeBias"/> as front percent (LMU
/// <c>(1 - mRearBrakeBias)·100</c>, iRacing <c>dcBrakeBias</c>, ACC raw front fraction ×100 without the display
/// offset).
/// </para>
/// <para>
/// Allocation-free: <see cref="IsSupported"/>, <see cref="TryRead"/> and <see cref="TryGetMax"/> are called every
/// frame (cache the property path strings in the constructor). <see cref="Describe"/> caches its text and allocates
/// only when what it describes changed. Never throws; NaN/infinity count as unknown.
/// </para>
/// </summary>
internal interface IDialTelemetry
{
    /// <summary>Short source name for the UI, e.g. "LMU native", "iRacing", "ACC", "AC EVO", "Generic (SimHub)".</summary>
    string Name { get; }

    /// <summary>True when this sim exposes <paramref name="channel"/> at all (static per sim, not per car).</summary>
    bool IsSupported(DialChannel channel);

    /// <summary>
    /// The current value. False (and NaN) when the channel is unsupported, the property is missing (no car, menu) or
    /// the value is not finite.
    /// </summary>
    bool TryRead(DialChannel channel, out double value);

    /// <summary>The car's maximum value when the sim reports one (LMU <c>mTCMax</c>, ...); false (NaN) otherwise.</summary>
    bool TryGetMax(DialChannel channel, out double max);

    /// <summary>
    /// Diagnostics text for the Speed Dial setup section (which property path each channel uses and whether it is
    /// present). Cached: returns the same string instance until the resolution state changes.
    /// </summary>
    string Describe();
}
