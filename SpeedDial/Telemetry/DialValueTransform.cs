namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>How a raw SimHub property value is converted into the channel's unit (integer level or front percent).</summary>
internal enum DialValueTransform
{
    /// <summary>The raw value is already in the channel's unit (levels, iRacing/SimHub front percent).</summary>
    None = 0,

    /// <summary>Raw front fraction 0..1 → front percent (×100). ACC / AC Rally / AC EVO <c>Physics</c> brake bias.</summary>
    FrontFractionToPercent,

    /// <summary>Raw rear fraction 0..1 → front percent ((1 − x)·100). LMU <c>mRearBrakeBias</c>.</summary>
    RearFractionToFrontPercent,

    /// <summary>
    /// Front share of unverified unit: values below 1 are taken as a fraction (×100), larger ones as percent.
    /// AC EVO <c>electronics.brake_bias</c> fallback only.
    /// </summary>
    FractionOrPercent,
}
