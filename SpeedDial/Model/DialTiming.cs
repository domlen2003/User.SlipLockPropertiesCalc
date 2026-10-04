using DivebombLogistics.Core;

namespace DivebombLogistics.SpeedDial.Model;

/// <summary>
/// Global timing and safety limits of the dialer (<see cref="SpeedDialSettings.Timing"/>). All values are ints
/// (atomic in SimHub's 32-bit process); the UI still edits them through <c>ISpeedDialHost.EditSettings</c>.
/// Plain JSON DTO (public fields).
/// </summary>
public sealed class DialTiming
{
    /// <summary>Default <see cref="PressMs"/>.</summary>
    public const int DefaultPressMs = 70;

    /// <summary>Lowest <see cref="PressMs"/> (shorter presses are missed by games polling at 60 Hz).</summary>
    public const int MinPressMs = 20;

    /// <summary>Highest <see cref="PressMs"/>.</summary>
    public const int MaxPressMs = 1000;

    /// <summary>Default <see cref="GapMs"/>.</summary>
    public const int DefaultGapMs = 90;

    /// <summary>Lowest <see cref="GapMs"/>.</summary>
    public const int MinGapMs = 0;

    /// <summary>Highest <see cref="GapMs"/>.</summary>
    public const int MaxGapMs = 2000;

    /// <summary>Default <see cref="ConfirmTimeoutMs"/>.</summary>
    public const int DefaultConfirmTimeoutMs = 800;

    /// <summary>Lowest <see cref="ConfirmTimeoutMs"/>.</summary>
    public const int MinConfirmTimeoutMs = 100;

    /// <summary>Highest <see cref="ConfirmTimeoutMs"/>.</summary>
    public const int MaxConfirmTimeoutMs = 5000;

    /// <summary>Default <see cref="MaxStallPresses"/>.</summary>
    public const int DefaultMaxStallPresses = 3;

    /// <summary>Lowest <see cref="MaxStallPresses"/>.</summary>
    public const int MinMaxStallPresses = 1;

    /// <summary>Highest <see cref="MaxStallPresses"/>.</summary>
    public const int MaxMaxStallPresses = 20;

    /// <summary>Default <see cref="MaxPressesPerChannel"/>.</summary>
    public const int DefaultMaxPressesPerChannel = 150;

    /// <summary>Lowest <see cref="MaxPressesPerChannel"/>.</summary>
    public const int MinMaxPressesPerChannel = 1;

    /// <summary>Highest <see cref="MaxPressesPerChannel"/>.</summary>
    public const int MaxMaxPressesPerChannel = 1000;

    /// <summary>Default <see cref="GatePauseTimeoutMs"/>.</summary>
    public const int DefaultGatePauseTimeoutMs = 10000;

    /// <summary>Lowest <see cref="GatePauseTimeoutMs"/> (0 = cancel as soon as the gate closes).</summary>
    public const int MinGatePauseTimeoutMs = 0;

    /// <summary>Highest <see cref="GatePauseTimeoutMs"/>.</summary>
    public const int MaxGatePauseTimeoutMs = 600000;

    /// <summary>How long each role press is held (passed to <c>IRoleOutput.Press</c>).</summary>
    public int PressMs = DefaultPressMs;

    /// <summary>Pause after a confirmed change (or a stall) before the next press.</summary>
    public int GapMs = DefaultGapMs;

    /// <summary>How long after a press the dialer waits for the telemetry value to change before counting a stall.</summary>
    public int ConfirmTimeoutMs = DefaultConfirmTimeoutMs;

    /// <summary>Consecutive presses without a value change before the channel ends (NoResponse / LimitReached).</summary>
    public int MaxStallPresses = DefaultMaxStallPresses;

    /// <summary>Safety stop: presses per channel and job (result MaxPresses).</summary>
    public int MaxPressesPerChannel = DefaultMaxPressesPerChannel;

    /// <summary>A job paused by the gate (game paused, menu, replay) is cancelled after this long.</summary>
    public int GatePauseTimeoutMs = DefaultGatePauseTimeoutMs;

    /// <summary>Clamps every value into its documented range (out-of-range values are clamped, not reset).</summary>
    public void Normalize()
    {
        PressMs = Clamp(PressMs, MinPressMs, MaxPressMs);
        GapMs = Clamp(GapMs, MinGapMs, MaxGapMs);
        ConfirmTimeoutMs = Clamp(ConfirmTimeoutMs, MinConfirmTimeoutMs, MaxConfirmTimeoutMs);
        MaxStallPresses = Clamp(MaxStallPresses, MinMaxStallPresses, MaxMaxStallPresses);
        MaxPressesPerChannel = Clamp(MaxPressesPerChannel, MinMaxPressesPerChannel, MaxMaxPressesPerChannel);
        GatePauseTimeoutMs = Clamp(GatePauseTimeoutMs, MinGatePauseTimeoutMs, MaxGatePauseTimeoutMs);
    }

    /// <summary>Independent copy.</summary>
    public DialTiming DeepCopy() => new DialTiming
    {
        PressMs = PressMs,
        GapMs = GapMs,
        ConfirmTimeoutMs = ConfirmTimeoutMs,
        MaxStallPresses = MaxStallPresses,
        MaxPressesPerChannel = MaxPressesPerChannel,
        GatePauseTimeoutMs = GatePauseTimeoutMs,
    };

    private static int Clamp(int value, int min, int max) => (int)MathUtil.Clamp(value, min, max);
}
