namespace DivebombLogistics.SpeedDial.Engine;

/// <summary>Where the <see cref="Dialer"/> stands within the channel it is dialing (<see cref="ChannelRun.Phase"/>).</summary>
internal enum ChannelPhase
{
    /// <summary>The channel has not been looked at yet in this job.</summary>
    Begin = 0,

    /// <summary>Waiting for the channel's first valid telemetry value.</summary>
    Starting,

    /// <summary>Ready to judge the value and, once the gap has elapsed, to press again.</summary>
    Decide,

    /// <summary>A press was sent; waiting for the value to change or for the confirmation timeout.</summary>
    AwaitChange,
}
