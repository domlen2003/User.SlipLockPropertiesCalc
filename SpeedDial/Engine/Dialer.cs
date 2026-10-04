using System;
using DivebombLogistics.Core;
using DivebombLogistics.Framework;
using DivebombLogistics.SpeedDial.Model;

namespace DivebombLogistics.SpeedDial.Engine;

/// <summary>
/// Closed-loop dial state machine: turns a <see cref="DialRequest"/> (an absolute target per channel) into Control
/// Mapper role presses, one press at a time, each confirmed on the game's telemetry before the next one. Pure (no
/// SimHub), data thread only, allocation-free after construction.
/// <para>
/// Per job the channels are dialed one after the other in <see cref="SpeedDialSettings.FillChannelOrder"/> order.
/// Per channel: read the value (none for a confirmation timeout: <see cref="ChannelResult.NoTelemetry"/>); clamp the
/// target to the car's known maximum; done when the value <see cref="DialChannels.Matches"/> the target. Otherwise
/// press the role that moves the value toward the target according to the learned
/// <see cref="ChannelLearning.Direction"/>, wait until the value changes or <see cref="DialTiming.ConfirmTimeoutMs"/>
/// passes (a stall), wait <see cref="DialTiming.GapMs"/> after the change and after the button was released, and
/// decide again. <see cref="DialTiming.MaxStallPresses"/> stalls in a row end the channel with
/// <see cref="ChannelResult.LimitReached"/> when the value sits at a known limit (the car's reported maximum, the
/// channel's minimum, or a value the same run reached by pressing that way) and with
/// <see cref="ChannelResult.NoResponse"/> otherwise; <see cref="DialTiming.MaxPressesPerChannel"/> is a safety stop.
/// </para>
/// <para>
/// Learning (written into the car's <see cref="ChannelLearning"/>, reported through <see cref="LearningChanged"/>):
/// a single-press change records the step and the observed range and confirms the direction; a single-press move
/// the wrong way flips the direction once per channel run, tentatively: the flip is confirmed only when a press with
/// the flipped role moves the value the right way, and it is undone when that role stalls (the wrong-way step was
/// most likely the driver's button) or when the channel or job ends before it was proven. A direction that was
/// already confirmed when the run started survives a first wrong-way step (taken for a manual press, like a contrary
/// step after this run's own proof). A second wrong move ends the channel with
/// <see cref="ChannelResult.DirectionError"/> and restores the direction the run started with (two contradicting
/// observations prove nothing, and a misconfigured binding must not corrupt the learned value). A channel stuck at the
/// limit on the far side of the target (the minimum while raising, the reported maximum while lowering) with an
/// unproven direction tries the other role once: a reversed binding pushes the value into that limit, and from there
/// the other role can only move it toward the target.
/// </para>
/// <para>
/// Robustness (manual presses during a dial are expected): a change larger than <see cref="JumpStepFactor"/> learned
/// steps (or <see cref="MaxUnlearnedStep"/> before a step is known) is a jump (a manual press, a telemetry glitch or
/// a wrap-around) and never teaches step or direction. A single wrong-way step right after this run proved the
/// direction is taken for a manual press. Two wrong-way jumps from the same value in the same direction mean the
/// value wraps around there: the dialer dials back to that value and reports <see cref="ChannelResult.LimitReached"/>;
/// <see cref="MaxWrongWayJumps"/> other wrong-way jumps end the channel with a direction error. A press that carries
/// the value across the target without matching it ends the channel with <see cref="ChannelResult.ClosestPossible"/>
/// (one press back first when the previous value was closer; a jump across the target is dialed back once). The
/// verdict on a change is taken only after the gap, so a press that lands late is seen. A press still in flight when
/// a job is replaced (or cancelled) is settled before the next job reads that channel.
/// </para>
/// <para>
/// Gate: presses happen only while <c>gateOpen</c>. A running job becomes <see cref="DialState.Paused"/> while the
/// gate is closed (a pending confirmation wait restarts after the resume) and <see cref="DialState.Cancelled"/>
/// when it stays closed for <see cref="DialTiming.GatePauseTimeoutMs"/>.
/// </para>
/// </summary>
internal sealed class Dialer
{
    /// <summary>Converts the millisecond settings of <see cref="DialTiming"/> to seconds.</summary>
    private const double MsToSeconds = 0.001;

    /// <summary>Changes smaller than this are float noise, not a reaction to a press (same threshold as step learning).</summary>
    private const double ChangeEpsilon = ChannelLearning.MinStep;

    /// <summary>A change larger than this many learned steps cannot come from one press.</summary>
    private const double JumpStepFactor = 1.5;

    /// <summary>
    /// Largest change one press is believed to cause before the channel's step is learned (integer levels or brake
    /// bias percent points; real brake bias steps are 0.1..1 %). Protects the learned step from telemetry glitches.
    /// </summary>
    private const double MaxUnlearnedStep = 2.0;

    /// <summary>Step of integer channels assumed before one is learned (only to compare "the same value").</summary>
    private const double DefaultIntegerStep = 1.0;

    /// <summary>Wrong-way jumps not explained by a wrap-around after which a channel stops (runaway guard).</summary>
    private const int MaxWrongWayJumps = 3;

    /// <summary>Upper bound of channel transitions in one <see cref="Update"/>: every channel can finish once, plus the job end.</summary>
    private const int MaxTransitionsPerUpdate = DialChannels.Count + 1;

    /// <summary>Used when the caller passes no settings (never expected; keeps <see cref="Update"/> from throwing).</summary>
    private static readonly SpeedDialSettings FallbackSettings = CreateFallbackSettings();

    /// <summary>Used when the settings carry no timing object.</summary>
    private static readonly DialTiming DefaultTiming = new DialTiming();

    private readonly IRoleOutput roles;
    private readonly ILog log;
    private readonly DialStatus status = new DialStatus();
    private readonly double[] targets = new double[DialChannels.Count];
    private readonly DialChannel[] order = new DialChannel[DialChannels.Count];
    private readonly ChannelLearning[] scratchLearning = CreateScratchLearning();
    private readonly ChannelRun run = new ChannelRun();

    // ---- Job ----
    private int jobSerial;
    private int orderCount;

    /// <summary>Position in <see cref="order"/> of the channel being dialed; -1 = the order is not built for this job yet.</summary>
    private int orderIndex = -1;

    private double lastNow;
    private double pausedAt = double.NaN;
    private bool learningChanged;

    /// <summary>No press before this time: the previous button must be released for the gap and the last change must have had the gap to settle.</summary>
    private double nextPressAllowedAt = double.NegativeInfinity;

    // ---- The press waiting for confirmation (survives Start/Cancel so a replaced job's press settles first) ----
    private bool pressPending;
    private int pressJob;
    private DialChannel pressChannel;
    private double pressValue;
    private double pressDeadline;

    // ---- Timing of the current Update, from DialTiming ----
    private int pressMs = DialTiming.DefaultPressMs;
    private double pressSeconds;
    private double gapSeconds;
    private double confirmSeconds;
    private int maxStallPresses;
    private int maxPressesPerChannel;
    private double gatePauseSeconds;

    /// <summary>Creates an idle dialer.</summary>
    /// <param name="roles">Where presses go (Control Mapper); required.</param>
    /// <param name="log">Log for rare events (direction learned, direction error, wrap-around); null = no log.</param>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is null.</exception>
    public Dialer(IRoleOutput roles, ILog log)
    {
        this.roles = roles ?? throw new ArgumentNullException(nameof(roles));
        this.log = log ?? NullLog.Instance;
        run.Reset(0.0);
        LoadTiming(null);
    }

    /// <summary>The live status of the current or last job (never replaced; data thread only; copy it with <see cref="DialStatus.CopyTo"/>).</summary>
    public DialStatus Status => status;

    /// <summary>True while a job runs or is paused (<see cref="DialStatus.IsBusy"/>).</summary>
    public bool IsBusy => status.IsBusy;

    /// <summary>True when the learning target passed to <see cref="Update"/> was modified since <see cref="ClearLearningChanged"/>.</summary>
    public bool LearningChanged => learningChanged;

    /// <summary>Acknowledges <see cref="LearningChanged"/> (the module marked the car data for saving).</summary>
    public void ClearLearningChanged() => learningChanged = false;

    /// <summary>
    /// Starts a job, replacing any running one without reporting it as cancelled (the status simply becomes the new
    /// job). Copies label, preset id and targets, so the caller may reuse <paramref name="request"/>. Channels
    /// without a target are <see cref="ChannelResult.Skipped"/>; a request without targets finishes at once as
    /// <see cref="DialState.Completed"/> ("Nothing to dial"). Never presses. Null is ignored. Allocation-free.
    /// </summary>
    /// <param name="request">What to dial.</param>
    /// <param name="now">Monotonic seconds.</param>
    public void Start(DialRequest request, double now)
    {
        if (request == null)
        {
            return;
        }

        if (!MathUtil.IsFinite(now))
        {
            now = lastNow;
        }

        lastNow = now;
        jobSerial++;
        int targetCount = 0;
        for (int i = 0; i < DialChannels.Count; i++)
        {
            var channel = (DialChannel)i;
            double requested = request.Targets[i];
            double target = MathUtil.IsFinite(requested) ? DialChannels.Sanitize(channel, requested) : double.NaN;
            targets[i] = target;
            scratchLearning[i].Reset();

            ChannelProgress progress = status.Channels[i];
            progress.Reset();
            if (double.IsNaN(target))
            {
                progress.Result = ChannelResult.Skipped;
            }
            else
            {
                progress.Target = target;
                targetCount++;
            }
        }

        status.Label = request.Label ?? string.Empty;
        status.PresetId = request.PresetId;
        status.CurrentChannel = null;
        status.StartedAt = now;
        if (targetCount == 0)
        {
            status.State = DialState.Completed;
            status.Message = DialerMessages.NothingToDial;
            status.FinishedAt = now;
        }
        else
        {
            status.State = DialState.Running;
            status.Message = DialerMessages.Starting;
            status.FinishedAt = double.NaN;
        }

        status.Version++;
        orderIndex = -1;
        pausedAt = double.NaN;
        UndoUnprovenFlip(); // a replaced job must not leave its unproven flip behind
        run.Reset(now);
    }

    /// <summary>
    /// Cancels the running or paused job: state <see cref="DialState.Cancelled"/>, message
    /// <paramref name="reason"/> (null → "Cancelled"; pass cached strings), the current and every still pending
    /// channel <see cref="ChannelResult.Cancelled"/>. No-op when not busy. A press already sent may still land.
    /// </summary>
    public void Cancel(string reason)
    {
        if (status.IsBusy)
        {
            CancelJob(reason ?? DialerMessages.Cancelled, lastNow);
        }
    }

    /// <summary>
    /// Advances the job by one frame; returns at once when not busy. Presses only while <paramref name="gateOpen"/>.
    /// Never throws for bad telemetry or settings (exceptions of the role output reach the caller). Allocation-free.
    /// </summary>
    /// <param name="now">Monotonic seconds (<c>frame.WallTime</c>); a non-finite value skips the frame.</param>
    /// <param name="gateOpen">Game running, not paused, not in a menu, not a replay, not spectating.</param>
    /// <param name="telemetry">The sim's dial telemetry; null ends the current channel with NoTelemetry.</param>
    /// <param name="settings">Global settings (bindings, timing, order); never null in practice.</param>
    /// <param name="learningTarget">The car's data whose <see cref="SpeedDialCarData.Learning"/> is used and updated; null (no car) = internal scratch learning, reset on every start and never reported.</param>
    public void Update(double now, bool gateOpen, IDialTelemetry telemetry, SpeedDialSettings settings, SpeedDialCarData learningTarget)
    {
        if (!MathUtil.IsFinite(now))
        {
            return;
        }

        lastNow = now;
        if (!status.IsBusy)
        {
            return;
        }

        SpeedDialSettings active = settings ?? FallbackSettings;
        LoadTiming(active.Timing);
        if (!gateOpen)
        {
            HoldForGate(now);
            return;
        }

        if (status.State == DialState.Paused)
        {
            Resume(now);
        }

        if (pressPending && !OwnPressPending() && !SettleForeignPress(now, telemetry))
        {
            return;
        }

        if (orderIndex < 0)
        {
            orderCount = active.FillChannelOrder(order);
            orderIndex = 0;
            run.Reset(now);
        }

        for (int transition = 0; transition < MaxTransitionsPerUpdate; transition++)
        {
            if (orderIndex >= orderCount)
            {
                FinishJob(now);
                return;
            }

            if (!StepChannel(order[orderIndex], now, telemetry, active, learningTarget))
            {
                return;
            }

            orderIndex++;
            run.Phase = ChannelPhase.Begin;
        }
    }

    // =====================================================================================================
    // Channel steps
    // =====================================================================================================

    /// <summary>Advances the current channel. Returns true when it finished (its result is set), false while it needs more frames.</summary>
    private bool StepChannel(DialChannel channel, double now, IDialTelemetry telemetry, SpeedDialSettings settings, SpeedDialCarData learningTarget)
    {
        ChannelProgress progress = status.Channels[(int)channel];
        if (run.Phase == ChannelPhase.Begin)
        {
            if (progress.Result != ChannelResult.Pending)
            {
                return true; // no target: Skipped by Start
            }

            ChannelBinding binding = settings.GetBinding(channel);
            if (binding != null && !binding.Enabled)
            {
                FinishChannel(progress, ChannelResult.Skipped);
                return true;
            }

            ShowChannel(channel);
            run.Reset(now);
            run.Phase = ChannelPhase.Starting;
        }

        if (telemetry == null || !telemetry.IsSupported(channel))
        {
            FinishChannel(progress, ChannelResult.NoTelemetry);
            return true;
        }

        ChannelLearning learning = ResolveLearning(channel, learningTarget);
        bool valid = telemetry.TryRead(channel, out double value) && MathUtil.IsFinite(value);
        if (valid)
        {
            SetCurrent(progress, value);
        }

        switch (run.Phase)
        {
            case ChannelPhase.Starting:
                if (!valid)
                {
                    return WaitForTelemetry(progress, now);
                }

                BeginRun(channel, progress, value, telemetry);
                break;

            case ChannelPhase.AwaitChange:
                if (valid && Math.Abs(value - run.PrePress) >= ChangeEpsilon)
                {
                    EndPress(now);
                    ChannelResult afterChange = OnChange(channel, value, learning);
                    if (afterChange != ChannelResult.Pending)
                    {
                        FinishChannel(progress, afterChange);
                        return true;
                    }
                }
                else if (now >= pressDeadline)
                {
                    EndPress(now);
                    if (valid)
                    {
                        ChannelResult afterStall = OnStall(channel, learning);
                        if (afterStall != ChannelResult.Pending)
                        {
                            FinishChannel(progress, afterStall);
                            return true;
                        }
                    }

                    // Without a value there is no verdict on this press; Decide waits for the telemetry.
                }
                else
                {
                    return false;
                }

                run.Phase = ChannelPhase.Decide;
                break;
        }

        return Decide(channel, progress, now, valid, value, settings, learning);
    }

    /// <summary>First valid value of the channel: remember it and fix the (clamped) target.</summary>
    private void BeginRun(DialChannel channel, ChannelProgress progress, double value, IDialTelemetry telemetry)
    {
        double target = targets[(int)channel];
        double min = DialChannels.MinValue(channel);
        if (telemetry.TryGetMax(channel, out double max) && MathUtil.IsFinite(max) && max >= min)
        {
            run.Max = max;
            target = MathUtil.Clamp(target, min, max);
        }

        run.EffectiveTarget = target;
        run.MissingSince = double.NaN;
        run.Phase = ChannelPhase.Decide;
        progress.Start = value;
        progress.Target = target;
        status.Version++;
    }

    /// <summary>Judges the value and presses when needed. Returns true when the channel finished.</summary>
    private bool Decide(
        DialChannel channel,
        ChannelProgress progress,
        double now,
        bool valid,
        double value,
        SpeedDialSettings settings,
        ChannelLearning learning)
    {
        // The gap comes first: it keeps the released button up long enough and lets a late change land before the verdict.
        if (now < nextPressAllowedAt)
        {
            return false;
        }

        if (!valid)
        {
            return WaitForTelemetry(progress, now);
        }

        run.MissingSince = double.NaN;
        double target = run.EffectiveTarget;
        if (DialChannels.Matches(channel, value, target, learning.Step))
        {
            FinishChannel(progress, run.LimitApplied ? ChannelResult.LimitReached : ChannelResult.Reached);
            return true;
        }

        if (run.HasPressed && CrossedForGood(value, learning))
        {
            FinishChannel(progress, ChannelResult.ClosestPossible);
            return true;
        }

        if (progress.Presses >= maxPressesPerChannel)
        {
            FinishChannel(progress, ChannelResult.MaxPresses);
            return true;
        }

        ChannelBinding binding = settings.GetBinding(channel);
        if (binding != null && !binding.Enabled)
        {
            FinishChannel(progress, ChannelResult.Skipped); // disabled by the user while dialing: stop pressing at once
            return true;
        }

        bool increase = (target - value) * learning.Direction > 0.0;
        string role = binding == null ? string.Empty : binding.GetRole(increase);
        if (role.Length == 0 || !roles.Press(role, pressMs))
        {
            FinishChannel(progress, ChannelResult.NoBinding);
            return true;
        }

        progress.Presses++;
        status.Version++;
        run.HasPressed = true;
        run.PrePress = value;
        run.PressSign = target > value ? 1 : -1;
        run.Phase = ChannelPhase.AwaitChange;
        pressPending = true;
        pressJob = jobSerial;
        pressChannel = channel;
        pressValue = value;
        pressDeadline = now + confirmSeconds;
        nextPressAllowedAt = now + pressSeconds + gapSeconds;
        return false;
    }

    /// <summary>The value changed after a press: learn from it. Returns Pending to go on, or the channel's final result.</summary>
    private ChannelResult OnChange(DialChannel channel, double value, ChannelLearning learning)
    {
        double delta = value - run.PrePress;
        bool jump = IsJump(delta, learning);
        bool changed = false;
        ChannelResult result = ChannelResult.Pending;
        run.StallCount = 0;
        if ((delta > 0.0 ? 1 : -1) == run.PressSign)
        {
            run.WrongStreak = 0;
            if (!jump)
            {
                changed |= learning.RecordStep(delta);
                changed |= learning.Observe(value);
                if (!learning.DirectionConfirmed)
                {
                    // First proof for this car (or a trial flip that worked): worth one log line.
                    learning.DirectionConfirmed = true;
                    changed = true;
                    if (run.LearningIsPersistent)
                    {
                        log.Info(DialerMessages.DirectionLog(channel, learning.Direction));
                    }
                }

                run.Verified = true;
                run.FlipUnproven = false;
                if (delta > 0.0)
                {
                    run.MovedUp = true;
                }
                else
                {
                    run.MovedDown = true;
                }
            }
        }
        else if (jump)
        {
            result = OnWrongWayJump(channel, learning);
        }
        else
        {
            result = OnWrongWayStep(channel, delta, value, learning, ref changed);
        }

        if (changed)
        {
            ReportLearning();
        }

        return result;
    }

    /// <summary>One step the wrong way: a manual press, or the learned direction is wrong.</summary>
    private ChannelResult OnWrongWayStep(DialChannel channel, double delta, double value, ChannelLearning learning, ref bool changed)
    {
        run.WrongStreak++;
        if ((run.Verified || (run.ConfirmedAtStart && !run.Flipped)) && run.WrongStreak == 1)
        {
            // This run's own presses (or an earlier, confirmed run) proved the direction: a single contrary step is
            // most likely the driver's button.
            return ChannelResult.Pending;
        }

        if (run.Flipped)
        {
            changed |= RestoreDirection(learning);
            log.Warn(DialerMessages.DirectionErrorLog(channel));
            return ChannelResult.DirectionError;
        }

        // Tentative: one unverified wrong-way step may just as well be a manual press that landed together with (or
        // instead of) the dialer's press. OnChange confirms the flip when the flipped role moves the value the right
        // way; OnStall and the end of the channel undo it otherwise.
        learning.Direction = Opposite(learning.Direction);
        learning.DirectionConfirmed = false;
        learning.RecordStep(delta);
        learning.Observe(value);
        changed = true;
        run.Flipped = true;
        run.FlipUnproven = true;
        run.StepFlip = true;
        run.Verified = false;
        return ChannelResult.Pending;
    }

    /// <summary>A jump the wrong way: a manual change or glitch, or the value wrapped around at a limit.</summary>
    private ChannelResult OnWrongWayJump(DialChannel channel, ChannelLearning learning)
    {
        if (run.HasJump && run.JumpSign == run.PressSign
            && Math.Abs(run.PrePress - run.JumpFrom) <= SameValueTolerance(channel, learning))
        {
            // Pressing the same way from the same value jumped the wrong way twice: the value wraps around there, so
            // that value is the closest the channel gets to a target beyond it.
            run.EffectiveTarget = run.PrePress;
            run.LimitApplied = true;
            run.HasJump = false;
            log.Info(DialerMessages.WrapLog(channel));
            return ChannelResult.Pending;
        }

        run.WrongWayJumps++;
        if (run.WrongWayJumps >= MaxWrongWayJumps)
        {
            if (run.Flipped && RestoreDirection(learning))
            {
                ReportLearning();
            }

            log.Warn(DialerMessages.DirectionErrorLog(channel));
            return ChannelResult.DirectionError;
        }

        run.HasJump = true;
        run.JumpFrom = run.PrePress;
        run.JumpSign = run.PressSign;
        return ChannelResult.Pending;
    }

    /// <summary>No change within the confirmation timeout. Returns Pending to press again, or the channel's final result.</summary>
    private ChannelResult OnStall(DialChannel channel, ChannelLearning learning)
    {
        run.StallCount++;
        if (run.StallCount < maxStallPresses)
        {
            return ChannelResult.Pending;
        }

        if (run.OvershootPending)
        {
            return ChannelResult.ClosestPossible; // the press back did not move: keep the value just past the target
        }

        if (run.StepFlip && run.FlipUnproven)
        {
            // The role a wrong-way step flipped to never moved the value: that step was most likely the driver's
            // button. Back to the start direction and dial on with it (the run keeps Flipped: no second flip).
            run.StepFlip = false;
            run.StallCount = 0;
            if (RestoreDirection(learning))
            {
                ReportLearning();
            }

            return ChannelResult.Pending;
        }

        if (CanTryOtherDirection(channel, learning))
        {
            // Stuck at the limit on the far side of the target with an unproven direction: the role most likely
            // pushes the value into that limit (reversed binding). The other role can only move it toward the target.
            learning.Direction = Opposite(learning.Direction);
            run.Flipped = true;
            run.FlipUnproven = true;
            run.TrialFlip = true;
            run.StallCount = 0;
            ReportLearning();
            return ChannelResult.Pending;
        }

        if (run.FlipUnproven && RestoreDirection(learning))
        {
            ReportLearning(); // neither role moved the value: the trial proved nothing
        }

        return IsAtLimit(channel) ? ChannelResult.LimitReached : ChannelResult.NoResponse;
    }

    /// <summary>
    /// True when the stalled value sits at the limit on the far side of the target (the channel minimum while
    /// raising, the reported maximum while lowering) and the direction is unproven, so trying the other role is safe.
    /// </summary>
    private bool CanTryOtherDirection(DialChannel channel, ChannelLearning learning)
    {
        if (run.Flipped || learning.DirectionConfirmed)
        {
            return false;
        }

        double value = run.PrePress;
        return run.PressSign > 0
            ? value <= DialChannels.MinValue(channel) + ChangeEpsilon
            : MathUtil.IsFinite(run.Max) && value >= run.Max - ChangeEpsilon;
    }

    /// <summary>
    /// The last press carried the value across the target without matching it. True when the current value is the
    /// closest possible; false to keep dialing (a jump across the target is dialed back once; when the value before
    /// the press was closer, one press back is allowed and the next crossing ends the channel).
    /// </summary>
    private bool CrossedForGood(double value, ChannelLearning learning)
    {
        double before = run.EffectiveTarget - run.PrePress;
        double after = run.EffectiveTarget - value;
        if (!(before * after < 0.0))
        {
            return false;
        }

        run.Crossings++;
        if (run.Crossings == 1 && IsJump(value - run.PrePress, learning))
        {
            return false;
        }

        if (run.OvershootPending || Math.Abs(after) <= Math.Abs(before))
        {
            return true;
        }

        run.OvershootPending = true;
        return false;
    }

    /// <summary>
    /// True when the stalled value sits at a known limit in the direction the dialer pressed: the car's reported
    /// maximum, the channel's minimum, or a value this run reached by pressing that way (the channel responded, then
    /// stopped). A channel that never moved in this run is NoResponse: the learned range only shows where the value
    /// has been, not where it ends.
    /// </summary>
    private bool IsAtLimit(DialChannel channel)
    {
        double value = run.PrePress;
        if (run.PressSign > 0)
        {
            return (MathUtil.IsFinite(run.Max) && value >= run.Max - ChangeEpsilon) || run.MovedUp;
        }

        return value <= DialChannels.MinValue(channel) + ChangeEpsilon || run.MovedDown;
    }

    /// <summary>No valid value: wait for one as long as for a press confirmation, then end with NoTelemetry. True when finished.</summary>
    private bool WaitForTelemetry(ChannelProgress progress, double now)
    {
        if (double.IsNaN(run.MissingSince))
        {
            run.MissingSince = now;
        }

        if (now - run.MissingSince < confirmSeconds)
        {
            return false;
        }

        FinishChannel(progress, ChannelResult.NoTelemetry);
        return true;
    }

    // =====================================================================================================
    // Job, gate and press bookkeeping
    // =====================================================================================================

    private void FinishJob(double now)
    {
        int successes = 0;
        int failures = 0;
        DialChannel firstFailure = DialChannel.Tc1;
        ChannelResult firstResult = ChannelResult.Pending;
        for (int i = 0; i < orderCount; i++)
        {
            DialChannel channel = order[i];
            ChannelResult result = status.Channels[(int)channel].Result;
            if (result == ChannelResult.Reached || result == ChannelResult.ClosestPossible)
            {
                successes++;
            }
            else if (result != ChannelResult.Skipped)
            {
                if (failures == 0)
                {
                    firstFailure = channel;
                    firstResult = result;
                }

                failures++;
            }
        }

        if (failures == 0)
        {
            status.State = DialState.Completed;
            status.Message = DialerMessages.Done;
        }
        else if (successes > 0)
        {
            status.State = DialState.Partial;
            status.Message = DialerMessages.Partial(firstFailure, firstResult);
        }
        else
        {
            status.State = DialState.Failed;
            status.Message = DialerMessages.Failed(firstFailure, firstResult);
        }

        status.CurrentChannel = null;
        status.FinishedAt = now;
        status.Version++;
        orderIndex = -1;
        run.Phase = ChannelPhase.Begin;
    }

    private void CancelJob(string message, double now)
    {
        UndoUnprovenFlip();
        for (int i = 0; i < status.Channels.Length; i++)
        {
            ChannelProgress progress = status.Channels[i];
            if (progress.Result == ChannelResult.Pending)
            {
                progress.Result = ChannelResult.Cancelled;
            }
        }

        status.State = DialState.Cancelled;
        status.Message = message;
        status.CurrentChannel = null;
        status.FinishedAt = now;
        status.Version++;
        orderIndex = -1;
        pausedAt = double.NaN;
        run.Phase = ChannelPhase.Begin;
    }

    /// <summary>Gate closed: pause a running job, cancel it when the pause lasts too long.</summary>
    private void HoldForGate(double now)
    {
        if (status.State == DialState.Running)
        {
            if (gatePauseSeconds <= 0.0)
            {
                CancelJob(DialerMessages.GateCancelled, now);
                return;
            }

            status.State = DialState.Paused;
            status.Message = DialerMessages.Paused;
            status.Version++;
            pausedAt = now;
            return;
        }

        if (now - pausedAt >= gatePauseSeconds)
        {
            CancelJob(DialerMessages.GateCancelled, now);
        }
    }

    /// <summary>Gate open again: back to Running; waits restart because the game may have ignored inputs meanwhile.</summary>
    private void Resume(double now)
    {
        status.State = DialState.Running;
        status.Message = status.CurrentChannel.HasValue
            ? DialerMessages.Dialing(status.CurrentChannel.Value)
            : DialerMessages.Starting;
        status.Version++;
        pausedAt = double.NaN;
        if (pressPending)
        {
            pressDeadline = now + confirmSeconds;
        }

        if (!double.IsNaN(run.MissingSince))
        {
            run.MissingSince = now;
        }

        nextPressAllowedAt = Math.Max(nextPressAllowedAt, now + gapSeconds);
    }

    /// <summary>True when the pending press belongs to the channel the current job is waiting on.</summary>
    private bool OwnPressPending() => pressJob == jobSerial && run.Phase == ChannelPhase.AwaitChange;

    /// <summary>
    /// A press of a replaced or cancelled job may still move its channel. When this job dials that channel, wait
    /// until the value changed or the confirmation timed out, so it never judges a value that is about to move.
    /// Returns true when settled.
    /// </summary>
    private bool SettleForeignPress(double now, IDialTelemetry telemetry)
    {
        if (!double.IsNaN(targets[(int)pressChannel]) && now < pressDeadline)
        {
            bool moved = telemetry != null
                && telemetry.TryRead(pressChannel, out double value)
                && MathUtil.IsFinite(value)
                && Math.Abs(value - pressValue) >= ChangeEpsilon;
            if (!moved)
            {
                return false;
            }
        }

        EndPress(now);
        return true;
    }

    /// <summary>The pending press is resolved (changed, stalled or settled): the gap starts now.</summary>
    private void EndPress(double now)
    {
        pressPending = false;
        nextPressAllowedAt = Math.Max(nextPressAllowedAt, now + gapSeconds);
    }

    /// <summary>The learning entry for this run: the car's entry, or scratch without a car. Re-bases the run when the entry changes.</summary>
    private ChannelLearning ResolveLearning(DialChannel channel, SpeedDialCarData learningTarget)
    {
        ChannelLearning persistent = learningTarget?.GetLearning(channel);
        ChannelLearning entry = persistent ?? scratchLearning[(int)channel];
        if (!ReferenceEquals(entry, run.Learning))
        {
            run.Learning = entry;
            run.LearningIsPersistent = persistent != null;
            run.DirectionAtStart = entry.Direction;
            run.ConfirmedAtStart = entry.DirectionConfirmed;
        }

        return entry;
    }

    /// <summary>Puts back the direction the run started with; true when that changed the entry.</summary>
    private bool RestoreDirection(ChannelLearning learning)
    {
        run.FlipUnproven = false;
        if (learning.Direction == run.DirectionAtStart && learning.DirectionConfirmed == run.ConfirmedAtStart)
        {
            return false;
        }

        learning.Direction = run.DirectionAtStart;
        learning.DirectionConfirmed = run.ConfirmedAtStart;
        return true;
    }

    /// <summary>
    /// The run ends (channel finished, job cancelled or replaced) while a flip of this run is still unproven (no press
    /// with the flipped role moved the value the right way): put the start direction back so a single observation can
    /// never persist. No-op when nothing was flipped, the flip was proven, or it was already undone.
    /// </summary>
    private void UndoUnprovenFlip()
    {
        if (run.FlipUnproven && run.Learning != null && RestoreDirection(run.Learning))
        {
            ReportLearning();
        }
    }

    /// <summary>Flags a change of the car's learning entry for saving (scratch learning without a car is never reported).</summary>
    private void ReportLearning()
    {
        if (run.LearningIsPersistent)
        {
            learningChanged = true;
        }
    }

    private void ShowChannel(DialChannel channel)
    {
        status.CurrentChannel = channel;
        status.Message = DialerMessages.Dialing(channel);
        status.Version++;
    }

    private void SetCurrent(ChannelProgress progress, double value)
    {
        if (progress.Current != value)
        {
            progress.Current = value;
            status.Version++;
        }
    }

    private void FinishChannel(ChannelProgress progress, ChannelResult result)
    {
        UndoUnprovenFlip();
        progress.Result = result;
        status.Version++;
    }

    /// <summary>Copies the timing settings, clamped like <see cref="DialTiming.Normalize"/> so unnormalized input cannot break the loop.</summary>
    private void LoadTiming(DialTiming timing)
    {
        timing ??= DefaultTiming;
        pressMs = Clamp(timing.PressMs, DialTiming.MinPressMs, DialTiming.MaxPressMs);
        pressSeconds = pressMs * MsToSeconds;
        gapSeconds = Clamp(timing.GapMs, DialTiming.MinGapMs, DialTiming.MaxGapMs) * MsToSeconds;

        // A press cannot be expected to show before it was held, so the timeout never ends before the button is released.
        int confirmMs = Clamp(timing.ConfirmTimeoutMs, DialTiming.MinConfirmTimeoutMs, DialTiming.MaxConfirmTimeoutMs);
        confirmSeconds = Math.Max(confirmMs, pressMs) * MsToSeconds;
        maxStallPresses = Clamp(timing.MaxStallPresses, DialTiming.MinMaxStallPresses, DialTiming.MaxMaxStallPresses);
        maxPressesPerChannel = Clamp(timing.MaxPressesPerChannel, DialTiming.MinMaxPressesPerChannel, DialTiming.MaxMaxPressesPerChannel);
        gatePauseSeconds = Clamp(timing.GatePauseTimeoutMs, DialTiming.MinGatePauseTimeoutMs, DialTiming.MaxGatePauseTimeoutMs)
            * MsToSeconds;
    }

    /// <summary>The other <see cref="ChannelLearning.Direction"/> value.</summary>
    private static int Opposite(int direction) =>
        direction == ChannelLearning.IncreaseLowers ? ChannelLearning.IncreaseRaises : ChannelLearning.IncreaseLowers;

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>A change this large cannot come from one press (see <see cref="JumpStepFactor"/>, <see cref="MaxUnlearnedStep"/>).</summary>
    private static bool IsJump(double delta, ChannelLearning learning)
    {
        double limit = learning.HasStep() ? learning.Step * JumpStepFactor : MaxUnlearnedStep;
        return Math.Abs(delta) > limit + ChangeEpsilon;
    }

    /// <summary>Two values closer than half a step are the same position of the channel.</summary>
    private static double SameValueTolerance(DialChannel channel, ChannelLearning learning)
    {
        double step = learning.HasStep()
            ? learning.Step
            : DialChannels.Kind(channel) == DialChannelKind.Integer ? DefaultIntegerStep : MaxUnlearnedStep;
        return step * 0.5;
    }

    private static ChannelLearning[] CreateScratchLearning()
    {
        var entries = new ChannelLearning[DialChannels.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new ChannelLearning();
        }

        return entries;
    }

    private static SpeedDialSettings CreateFallbackSettings()
    {
        var settings = new SpeedDialSettings();
        settings.Normalize();
        return settings;
    }
}
