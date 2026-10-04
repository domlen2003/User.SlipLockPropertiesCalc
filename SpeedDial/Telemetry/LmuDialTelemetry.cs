using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.SpeedDial.Engine;

namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>
/// Le Mans Ultimate dial channels from SimHub's native player telemetry (<c>PlayerNativeTelemetry</c>, LMU's own
/// <c>TelemInfoV01</c>): TC, TC Power Cut, TC Slip Angle and ABS as byte levels with per-car maxima (<c>m*Max</c>,
/// 0 = not adjustable), brake bias from the rear fraction <c>mRearBrakeBias</c> as front percent.
/// </summary>
internal sealed class LmuDialTelemetry : IDialTelemetry
{
    /// <summary>Diagnostics header; the prefix's trailing dot is dropped so it reads as a path, not a sentence end.</summary>
    private static readonly string Header = "LMU native (" + DialPropertyPaths.LmuPrefix.TrimEnd('.') + ")";

    private readonly DialChannelSet set;

    /// <summary>Creates the reader (allocates; game change only). <paramref name="reader"/> is required.</summary>
    public LmuDialTelemetry(ITelemetryReader reader)
    {
        set = new DialChannelSet(
            reader,
            Header,
            DialChannelSource.Single(DialChannel.Tc1, DialPropertyPaths.LmuTc, DialValueTransform.None, DialPropertyPaths.LmuTcMax),
            DialChannelSource.Single(DialChannel.Tc2, DialPropertyPaths.LmuTcCut, DialValueTransform.None, DialPropertyPaths.LmuTcCutMax),
            DialChannelSource.Single(DialChannel.Tc3, DialPropertyPaths.LmuTcSlip, DialValueTransform.None, DialPropertyPaths.LmuTcSlipMax),
            DialChannelSource.Single(DialChannel.Abs, DialPropertyPaths.LmuAbs, DialValueTransform.None, DialPropertyPaths.LmuAbsMax),
            DialChannelSource.Single(
                DialChannel.BrakeBias,
                DialPropertyPaths.LmuRearBrakeBias,
                DialValueTransform.RearFractionToFrontPercent,
                null));
    }

    /// <inheritdoc />
    public string Name => "LMU native";

    /// <inheritdoc />
    public bool IsSupported(DialChannel channel) => set.IsSupported(channel);

    /// <inheritdoc />
    public bool TryRead(DialChannel channel, out double value) => set.TryRead(channel, out value);

    /// <inheritdoc />
    public bool TryGetMax(DialChannel channel, out double max) => set.TryGetMax(channel, out max);

    /// <inheritdoc />
    public string Describe() => set.Describe();
}
