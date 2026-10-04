using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.SpeedDial.Engine;

namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>
/// iRacing dial channels from the in-car adjustment variables (<c>dcTractionControl</c>, <c>dcTractionControl2</c>,
/// <c>dcABS</c>, <c>dcBrakeBias</c> = front percent). A car without a control has no such variable, so
/// <see cref="TryRead"/> is false for it. iRacing has no third TC map and reports no maxima.
/// </summary>
internal sealed class IRacingDialTelemetry : IDialTelemetry
{
    private const string Header = "iRacing (" + DialPropertyPaths.IRacingPrefix + ")";

    private readonly DialChannelSet set;

    /// <summary>Creates the reader (allocates; game change only). <paramref name="reader"/> is required.</summary>
    public IRacingDialTelemetry(ITelemetryReader reader)
    {
        set = new DialChannelSet(
            reader,
            Header,
            DialChannelSource.Single(DialChannel.Tc1, DialPropertyPaths.IRacingTc, DialValueTransform.None, null),
            DialChannelSource.Single(DialChannel.Tc2, DialPropertyPaths.IRacingTc2, DialValueTransform.None, null),
            DialChannelSource.Single(DialChannel.Abs, DialPropertyPaths.IRacingAbs, DialValueTransform.None, null),
            DialChannelSource.Single(DialChannel.BrakeBias, DialPropertyPaths.IRacingBrakeBias, DialValueTransform.None, null));
    }

    /// <inheritdoc />
    public string Name => "iRacing";

    /// <inheritdoc />
    public bool IsSupported(DialChannel channel) => set.IsSupported(channel);

    /// <inheritdoc />
    public bool TryRead(DialChannel channel, out double value) => set.TryRead(channel, out value);

    /// <inheritdoc />
    public bool TryGetMax(DialChannel channel, out double max) => set.TryGetMax(channel, out max);

    /// <inheritdoc />
    public string Describe() => set.Describe();
}
