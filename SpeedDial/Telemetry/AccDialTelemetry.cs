using System;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.SpeedDial.Engine;

namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>
/// Assetto Corsa family dial channels.
/// <list type="bullet">
/// <item>ACC and AC Rally: <c>Graphics.TC</c>, <c>Graphics.TCCut</c>, <c>Graphics.ABS</c> (int levels) and
/// <c>Physics.BrakeBias</c> (raw front fraction ×100, without the car-specific offset the game's display adds).
/// No maxima.</item>
/// <item>AC EVO: the integer levels of <c>Graphics.electronics</c> (<c>tc_level</c>, <c>tc_cut_level</c>,
/// <c>abs_level</c>) with the per-car limits of <c>Graphics.electronics_max_limit</c>, falling back to the
/// lower-camel <c>Physics.tc</c> / <c>Physics.abs</c>; brake bias from <c>Physics.brakeBias</c> (front fraction
/// ×100), falling back to <c>electronics.brake_bias</c>.</item>
/// </list>
/// None of these sims has a third TC map.
/// </summary>
internal sealed class AccDialTelemetry : IDialTelemetry
{
    private const string AccName = "ACC";
    private const string EvoName = "AC EVO";
    private const string RallyName = "AC Rally";

    private const string AccHeader = AccName + " (" + DialPropertyPaths.GameRawDataPrefix + "Graphics/Physics)";
    private const string EvoHeader = EvoName + " (" + DialPropertyPaths.GameRawDataPrefix + "Graphics.electronics/Physics)";
    private const string RallyHeader = RallyName + " (" + DialPropertyPaths.GameRawDataPrefix + "Graphics/Physics)";

    private readonly DialChannelSet set;

    /// <summary>
    /// Creates the reader (allocates; game change only). <paramref name="gameName"/> (case-insensitive) selects AC EVO
    /// or AC Rally; anything else reads like ACC. <paramref name="reader"/> is required.
    /// </summary>
    public AccDialTelemetry(ITelemetryReader reader, string gameName)
    {
        if (string.Equals(gameName, DialGameNames.AcEvo, StringComparison.OrdinalIgnoreCase))
        {
            Name = EvoName;
            set = CreateEvo(reader);
        }
        else
        {
            bool rally = string.Equals(gameName, DialGameNames.AcRally, StringComparison.OrdinalIgnoreCase);
            Name = rally ? RallyName : AccName;
            set = CreateAcc(reader, rally ? RallyHeader : AccHeader);
        }
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsSupported(DialChannel channel) => set.IsSupported(channel);

    /// <inheritdoc />
    public bool TryRead(DialChannel channel, out double value) => set.TryRead(channel, out value);

    /// <inheritdoc />
    public bool TryGetMax(DialChannel channel, out double max) => set.TryGetMax(channel, out max);

    /// <inheritdoc />
    public string Describe() => set.Describe();

    private static DialChannelSet CreateAcc(ITelemetryReader reader, string header) =>
        new DialChannelSet(
            reader,
            header,
            DialChannelSource.Single(DialChannel.Tc1, DialPropertyPaths.AccTc, DialValueTransform.None, null),
            DialChannelSource.Single(DialChannel.Tc2, DialPropertyPaths.AccTcCut, DialValueTransform.None, null),
            DialChannelSource.Single(DialChannel.Abs, DialPropertyPaths.AccAbs, DialValueTransform.None, null),
            DialChannelSource.Single(
                DialChannel.BrakeBias,
                DialPropertyPaths.AccBrakeBias,
                DialValueTransform.FrontFractionToPercent,
                null));

    private static DialChannelSet CreateEvo(ITelemetryReader reader) =>
        new DialChannelSet(
            reader,
            EvoHeader,
            DialChannelSource.WithFallback(
                DialChannel.Tc1,
                DialPropertyPaths.EvoTcLevel,
                DialValueTransform.None,
                DialPropertyPaths.EvoPhysicsTc,
                DialValueTransform.None,
                DialPropertyPaths.EvoTcMax),
            DialChannelSource.Single(DialChannel.Tc2, DialPropertyPaths.EvoTcCutLevel, DialValueTransform.None, DialPropertyPaths.EvoTcCutMax),
            DialChannelSource.WithFallback(
                DialChannel.Abs,
                DialPropertyPaths.EvoAbsLevel,
                DialValueTransform.None,
                DialPropertyPaths.EvoPhysicsAbs,
                DialValueTransform.None,
                DialPropertyPaths.EvoAbsMax),
            DialChannelSource.WithFallback(
                DialChannel.BrakeBias,
                DialPropertyPaths.EvoPhysicsBrakeBias,
                DialValueTransform.FrontFractionToPercent,
                DialPropertyPaths.EvoElectronicsBrakeBias,
                DialValueTransform.FractionOrPercent,
                null));
}
