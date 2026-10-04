namespace DivebombLogistics.SpeedDial.Telemetry;

/// <summary>What the most recent lookup of a channel's value (or maximum) property found. Drives the diagnostics text.</summary>
internal enum DialPathState
{
    /// <summary>Not looked up yet, or the channel has no such property (no maximum path).</summary>
    None = 0,

    /// <summary>The property does not exist or is not numeric (no car loaded, menu, the car lacks the control).</summary>
    Missing,

    /// <summary>The property exists but its value is not usable (non-finite, out of range, maximum ≤ 0).</summary>
    Invalid,

    /// <summary>The property exists and holds a usable value.</summary>
    Present,
}
