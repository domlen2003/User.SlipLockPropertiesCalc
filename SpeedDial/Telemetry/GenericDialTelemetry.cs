using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.SpeedDial.Engine;

namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>
/// Fallback for every game without a dedicated reader (also rFactor 2 and original AC): SimHub's normalized
/// <c>GameData.TCLevel</c>, <c>ABSLevel</c> and <c>BrakeBias</c> (front percent; 0 = not reported, treated as
/// unknown). TC2/TC3 are unsupported and no maxima are reported.
/// </summary>
internal sealed class GenericDialTelemetry : IDialTelemetry
{
    private const string Header = "Generic SimHub data (" + DialPropertyPaths.GameDataPrefix + ")";

    private readonly DialChannelSet set;

    /// <summary>Creates the reader (allocates; game change only). <paramref name="reader"/> is required.</summary>
    public GenericDialTelemetry(ITelemetryReader reader)
    {
        set = new DialChannelSet(
            reader,
            Header,
            DialChannelSource.Single(DialChannel.Tc1, DialPropertyPaths.GenericTcLevel, DialValueTransform.None, null),
            DialChannelSource.Single(DialChannel.Abs, DialPropertyPaths.GenericAbsLevel, DialValueTransform.None, null),
            DialChannelSource.Single(DialChannel.BrakeBias, DialPropertyPaths.GenericBrakeBias, DialValueTransform.None, null));
    }

    /// <inheritdoc />
    public string Name => "Generic (SimHub)";

    /// <inheritdoc />
    public bool IsSupported(DialChannel channel) => set.IsSupported(channel);

    /// <inheritdoc />
    public bool TryRead(DialChannel channel, out double value) => set.TryRead(channel, out value);

    /// <inheritdoc />
    public bool TryGetMax(DialChannel channel, out double max) => set.TryGetMax(channel, out max);

    /// <inheritdoc />
    public string Describe() => set.Describe();
}
