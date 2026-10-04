using System;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.SpeedDial.Engine;

namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>Picks the dial telemetry reader for a SimHub game name. Called on game change only.</summary>
internal static class DialTelemetryFactory
{
    /// <summary>
    /// <paramref name="gameName"/> case-insensitive: "LMU" → <see cref="LmuDialTelemetry"/>; "IRacing" →
    /// <see cref="IRacingDialTelemetry"/>; "AssettoCorsaCompetizione", "AssettoCorsaEVO", "AssettoCorsaRally" →
    /// <see cref="AccDialTelemetry"/>; anything else (also "RFactor2", "AssettoCorsa", null, empty) →
    /// <see cref="GenericDialTelemetry"/>. Allocates.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public static IDialTelemetry Create(string gameName, ITelemetryReader reader)
    {
        if (reader == null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        if (Is(gameName, DialGameNames.Lmu))
        {
            return new LmuDialTelemetry(reader);
        }

        if (Is(gameName, DialGameNames.IRacing))
        {
            return new IRacingDialTelemetry(reader);
        }

        if (Is(gameName, DialGameNames.Acc) || Is(gameName, DialGameNames.AcEvo) || Is(gameName, DialGameNames.AcRally))
        {
            return new AccDialTelemetry(reader, gameName);
        }

        return new GenericDialTelemetry(reader);
    }

    private static bool Is(string gameName, string expected) =>
        string.Equals(gameName, expected, StringComparison.OrdinalIgnoreCase);
}
