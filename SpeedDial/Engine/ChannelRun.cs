using DivebombLogistics.SpeedDial.Model;

namespace DivebombLogistics.SpeedDial.Engine;

/// <summary>
/// Bookkeeping of the one channel the <see cref="Dialer"/> is dialing right now. Channels are dialed one after the
/// other, so a single preallocated instance is reused for every channel of every job (<see cref="Reset"/>).
/// Data thread only; plain fields on purpose (hot path, no allocation).
/// </summary>
internal sealed class ChannelRun
{
    /// <summary>Current phase.</summary>
    public ChannelPhase Phase;

    /// <summary>The learning entry used by this run (the car's entry, or the dialer's scratch entry without a car); null until resolved.</summary>
    public ChannelLearning Learning;

    /// <summary>True when <see cref="Learning"/> belongs to the car data, so changes must be reported for saving.</summary>
    public bool LearningIsPersistent;

    /// <summary><see cref="ChannelLearning.Direction"/> when the run started (restored after a direction error).</summary>
    public int DirectionAtStart;

    /// <summary><see cref="ChannelLearning.DirectionConfirmed"/> when the run started.</summary>
    public bool ConfirmedAtStart;

    /// <summary>The value being dialed to: the clamped target, or a wrap-around limit in front of it (<see cref="LimitApplied"/>).</summary>
    public double EffectiveTarget;

    /// <summary>True when <see cref="EffectiveTarget"/> was pulled back to the value where the channel wraps around.</summary>
    public bool LimitApplied;

    /// <summary>The car's maximum reported by the telemetry; NaN when unknown.</summary>
    public double Max;

    /// <summary>True once this run sent a press.</summary>
    public bool HasPressed;

    /// <summary>Value right before the latest press.</summary>
    public double PrePress;

    /// <summary>Which way the latest press should move the value: +1 up, -1 down (toward the target).</summary>
    public int PressSign;

    /// <summary>Presses in a row that did not change the value.</summary>
    public int StallCount;

    /// <summary>Single-step moves in a row that went the wrong way.</summary>
    public int WrongStreak;

    /// <summary>True when a press of this run moved the value as the current direction predicts.</summary>
    public bool Verified;

    /// <summary>True after this run flipped the learned direction (only one flip per run).</summary>
    public bool Flipped;

    /// <summary>True when the flip was a trial after stalls at the far limit (unconfirmed until a press moves the value).</summary>
    public bool TrialFlip;

    /// <summary>True from a flip of this run until a press proves it (moves the value the right way) or the start direction is restored.</summary>
    public bool FlipUnproven;

    /// <summary>True while the flip came from a single wrong-way step and has not been tried back after a stall (unconfirmed until a press moves the value).</summary>
    public bool StepFlip;

    /// <summary>Wrong-way jumps that were not explained by a wrap-around.</summary>
    public int WrongWayJumps;

    /// <summary>True when a wrong-way jump was seen (<see cref="JumpFrom"/>/<see cref="JumpSign"/> are valid).</summary>
    public bool HasJump;

    /// <summary>Value before the last wrong-way jump.</summary>
    public double JumpFrom;

    /// <summary>Press direction (<see cref="PressSign"/>) of the last wrong-way jump.</summary>
    public int JumpSign;

    /// <summary>How often a press carried the value across the target without matching it.</summary>
    public int Crossings;

    /// <summary>True after an overshoot where the value before the press was closer: one press back, then stop.</summary>
    public bool OvershootPending;

    /// <summary>True when a press of this run raised the value as predicted (evidence for a limit when it later stalls).</summary>
    public bool MovedUp;

    /// <summary>True when a press of this run lowered the value as predicted.</summary>
    public bool MovedDown;

    /// <summary>Since when the telemetry has had no valid value; NaN while it is valid.</summary>
    public double MissingSince;

    /// <summary>Back to a fresh run that starts at <paramref name="now"/> (phase <see cref="ChannelPhase.Begin"/>).</summary>
    public void Reset(double now)
    {
        Phase = ChannelPhase.Begin;
        Learning = null;
        LearningIsPersistent = false;
        DirectionAtStart = ChannelLearning.IncreaseRaises;
        ConfirmedAtStart = false;
        EffectiveTarget = double.NaN;
        LimitApplied = false;
        Max = double.NaN;
        HasPressed = false;
        PrePress = double.NaN;
        PressSign = 0;
        StallCount = 0;
        WrongStreak = 0;
        Verified = false;
        Flipped = false;
        TrialFlip = false;
        StepFlip = false;
        FlipUnproven = false;
        WrongWayJumps = 0;
        HasJump = false;
        JumpFrom = double.NaN;
        JumpSign = 0;
        Crossings = 0;
        OvershootPending = false;
        MovedUp = false;
        MovedDown = false;
        MissingSince = now;
    }
}
