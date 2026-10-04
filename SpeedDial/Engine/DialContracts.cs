using DivebombLogistics.Core;
using DivebombLogistics.SpeedDial.Model;

namespace DivebombLogistics.SpeedDial.Engine;

// Contract file (SpeedDial Stage B): the dial job request and the status the Dialer reports. The request is internal
// (data thread only); the status types are public because the UI snapshot carries a copy.

/// <summary>State of the dialer's current or last job (<see cref="DialStatus.State"/>).</summary>
public enum DialState
{
    /// <summary>No job since start-up (or since the car changed).</summary>
    Idle = 0,

    /// <summary>A job is dialing.</summary>
    Running,

    /// <summary>A job waits for the gate (game paused, menu, replay); it resumes when the gate opens or is cancelled after <c>GatePauseTimeoutMs</c>.</summary>
    Paused,

    /// <summary>Finished: every targeted channel is <see cref="ChannelResult.Reached"/>, <see cref="ChannelResult.ClosestPossible"/> or <see cref="ChannelResult.Skipped"/>.</summary>
    Completed,

    /// <summary>Finished: some targeted channels reached their target, others failed.</summary>
    Partial,

    /// <summary>Finished: no targeted (non-skipped) channel reached its target.</summary>
    Failed,

    /// <summary>Cancelled by the user, a newer job's owner, a fault, the game stopping or the gate timeout.</summary>
    Cancelled,
}

/// <summary>Outcome of one channel within a job (<see cref="ChannelProgress.Result"/>).</summary>
public enum ChannelResult
{
    /// <summary>Not processed yet, or being dialed right now.</summary>
    Pending = 0,

    /// <summary>The value matches the target (<c>DialChannels.Matches</c>).</summary>
    Reached,

    /// <summary>The target lies between two possible values: the closest one was kept (counts as success).</summary>
    ClosestPossible,

    /// <summary>No valid telemetry value for the channel (unsupported, missing, NaN).</summary>
    NoTelemetry,

    /// <summary>The role needed is empty, or the press could not be queued (<c>IRoleOutput.Press</c> returned false, e.g. Control Mapper unavailable).</summary>
    NoBinding,

    /// <summary><c>MaxStallPresses</c> presses in a row did not change the value.</summary>
    NoResponse,

    /// <summary>The value stalled at the car's known maximum/minimum before reaching the target.</summary>
    LimitReached,

    /// <summary>The value moved away from the target again after the one allowed direction flip.</summary>
    DirectionError,

    /// <summary>Safety stop after <c>MaxPressesPerChannel</c> presses.</summary>
    MaxPresses,

    /// <summary>The job was cancelled before or while this channel was dialed.</summary>
    Cancelled,

    /// <summary>Not part of the job (no target) or the channel's binding is disabled. Neither success nor failure.</summary>
    Skipped,
}

/// <summary>
/// What a job should dial: a label for the UI and an absolute target per channel (NaN = leave the channel alone).
/// Preallocated and reusable: the module keeps one instance and refills it per job; <c>Dialer.Start</c> copies it, so
/// it may be reused right after. Data thread only. All members are allocation-free.
/// </summary>
internal sealed class DialRequest
{
    /// <summary>Target per channel, indexed by <c>(int)DialChannel</c>; NaN = skip. Length <see cref="DialChannels.Count"/>.</summary>
    public readonly double[] Targets = new double[DialChannels.Count];

    /// <summary>Creates an empty request (every target NaN).</summary>
    public DialRequest()
    {
        Clear(string.Empty);
    }

    /// <summary>Shown in the status and the <c>ActivePreset</c> property: the preset name, or e.g. "Reset Pair 1" (pass a cached string).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The preset being dialed, or null (pair reset, manual request). Informational for the module.</summary>
    public string PresetId { get; set; }

    /// <summary>Number of channels with a target.</summary>
    public int TargetCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < Targets.Length; i++)
            {
                if (!double.IsNaN(Targets[i]))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Removes every target and sets the label (null → empty); clears <see cref="PresetId"/>.</summary>
    public void Clear(string label)
    {
        for (int i = 0; i < Targets.Length; i++)
        {
            Targets[i] = double.NaN;
        }

        Label = label ?? string.Empty;
        PresetId = null;
    }

    /// <summary>The target of <paramref name="channel"/>; NaN when skipped or invalid.</summary>
    public double GetTarget(DialChannel channel) => DialChannels.IsValid(channel) ? Targets[(int)channel] : double.NaN;

    /// <summary>True when <paramref name="channel"/> has a target.</summary>
    public bool HasTarget(DialChannel channel) => !double.IsNaN(GetTarget(channel));

    /// <summary>Sets a target (sanitized with <c>DialChannels.Sanitize</c>); NaN or a non-finite value skips the channel.</summary>
    public void SetTarget(DialChannel channel, double value)
    {
        if (DialChannels.IsValid(channel))
        {
            Targets[(int)channel] = MathUtil.IsFinite(value) ? DialChannels.Sanitize(channel, value) : double.NaN;
        }
    }

    /// <summary>Copies label, preset id and targets from <paramref name="other"/>.</summary>
    public void CopyFrom(DialRequest other)
    {
        if (other == null)
        {
            Clear(string.Empty);
            return;
        }

        Label = other.Label;
        PresetId = other.PresetId;
        for (int i = 0; i < Targets.Length; i++)
        {
            Targets[i] = other.Targets[i];
        }
    }

    /// <summary>Fills the request from a preset: label = preset name, <see cref="PresetId"/> = its id, targets = its included values.</summary>
    public void LoadPreset(DialPreset preset)
    {
        Clear(preset?.Name);
        if (preset == null)
        {
            return;
        }

        PresetId = preset.Id;
        for (int i = 0; i < Targets.Length; i++)
        {
            var channel = (DialChannel)i;
            if (preset.TryGetValue(channel, out double value))
            {
                SetTarget(channel, value);
            }
        }
    }

    /// <summary>
    /// Fills the request for a pair reset: targets = the stored values of the channels the pair includes (stored
    /// values of channels removed from the pair since are ignored).
    /// </summary>
    /// <param name="label">E.g. "Reset Pair 1" (pass a cached string).</param>
    /// <param name="pair">The pair definition.</param>
    /// <param name="stored">The car's stored values for the pair.</param>
    public void LoadPairSnapshot(string label, SetResetPairDefinition pair, DialSnapshot stored)
    {
        Clear(label);
        if (pair == null || stored == null)
        {
            return;
        }

        for (int i = 0; i < Targets.Length; i++)
        {
            var channel = (DialChannel)i;
            if (pair.Includes(channel) && stored.TryGetValue(channel, out double value))
            {
                SetTarget(channel, value);
            }
        }
    }
}

/// <summary>Progress of one channel within the current/last job (<see cref="DialStatus.Channels"/>).</summary>
public sealed class ChannelProgress
{
    /// <summary>Value when the dialer started this channel; NaN until then or when unknown.</summary>
    public double Start = double.NaN;

    /// <summary>Target (after clamping to the car's known maximum); NaN when skipped.</summary>
    public double Target = double.NaN;

    /// <summary>Latest value seen by the dialer; NaN when unknown.</summary>
    public double Current = double.NaN;

    /// <summary>Role presses sent for this channel in this job.</summary>
    public int Presses;

    /// <summary>Outcome (Pending while not finished).</summary>
    public ChannelResult Result;

    /// <summary>Back to the initial state (NaN values, no presses, Pending).</summary>
    public void Reset()
    {
        Start = double.NaN;
        Target = double.NaN;
        Current = double.NaN;
        Presses = 0;
        Result = ChannelResult.Pending;
    }

    /// <summary>Copies every field into <paramref name="target"/> (allocation-free).</summary>
    public void CopyTo(ChannelProgress target)
    {
        target.Start = Start;
        target.Target = Target;
        target.Current = Current;
        target.Presses = Presses;
        target.Result = Result;
    }
}

/// <summary>
/// The dialer's observable state. The <c>Dialer</c> owns one live instance (data thread) and bumps
/// <see cref="Version"/> on every change; the module copies it into the UI snapshot with <see cref="CopyTo"/> under
/// its snapshot lock. Strings are assigned by reference only (cached constants or the request label), never built
/// per frame.
/// </summary>
public sealed class DialStatus
{
    /// <summary>Progress per channel, indexed by <c>(int)DialChannel</c> (length <see cref="DialChannels.Count"/>, entries never null).</summary>
    public readonly ChannelProgress[] Channels = CreateChannels();

    /// <summary>Job state.</summary>
    public DialState State;

    /// <summary>The job's label (preset name, "Reset Pair 1", ...); empty when idle.</summary>
    public string Label = string.Empty;

    /// <summary>The preset being/last dialed (<see cref="DialRequest.PresetId"/>); null for other jobs.</summary>
    public string PresetId;

    /// <summary>Channel being dialed while Running/Paused; null otherwise.</summary>
    public DialChannel? CurrentChannel;

    /// <summary>Human-readable state ("Dialing TC2 (Cut)", "Done", "Cancelled: game paused too long", ...).</summary>
    public string Message = string.Empty;

    /// <summary>Incremented on every change of any field (wraps around; compare for inequality).</summary>
    public int Version;

    /// <summary>Monotonic seconds when the job started; NaN when idle.</summary>
    public double StartedAt = double.NaN;

    /// <summary>Monotonic seconds when the job finished (Completed/Partial/Failed/Cancelled); NaN while running or idle.</summary>
    public double FinishedAt = double.NaN;

    /// <summary>True while a job runs or is paused.</summary>
    public bool IsBusy => State == DialState.Running || State == DialState.Paused;

    /// <summary>True when the last job ended (Completed, Partial, Failed or Cancelled).</summary>
    public bool IsFinished => State == DialState.Completed || State == DialState.Partial
        || State == DialState.Failed || State == DialState.Cancelled;

    /// <summary>The progress of <paramref name="channel"/> (null for an invalid value).</summary>
    public ChannelProgress Get(DialChannel channel) => DialChannels.IsValid(channel) ? Channels[(int)channel] : null;

    /// <summary>Presses of every channel in this job.</summary>
    public int TotalPresses()
    {
        int total = 0;
        for (int i = 0; i < Channels.Length; i++)
        {
            total += Channels[i].Presses;
        }

        return total;
    }

    /// <summary>Back to Idle (every channel reset, empty label and message) and bumps <see cref="Version"/>.</summary>
    public void Reset()
    {
        State = DialState.Idle;
        Label = string.Empty;
        PresetId = null;
        CurrentChannel = null;
        Message = string.Empty;
        StartedAt = double.NaN;
        FinishedAt = double.NaN;
        for (int i = 0; i < Channels.Length; i++)
        {
            Channels[i].Reset();
        }

        Version++;
    }

    /// <summary>Copies every field into <paramref name="target"/> (allocation-free).</summary>
    public void CopyTo(DialStatus target)
    {
        target.State = State;
        target.Label = Label;
        target.PresetId = PresetId;
        target.CurrentChannel = CurrentChannel;
        target.Message = Message;
        target.Version = Version;
        target.StartedAt = StartedAt;
        target.FinishedAt = FinishedAt;
        for (int i = 0; i < Channels.Length; i++)
        {
            Channels[i].CopyTo(target.Channels[i]);
        }
    }

    private static ChannelProgress[] CreateChannels()
    {
        var channels = new ChannelProgress[DialChannels.Count];
        for (int i = 0; i < channels.Length; i++)
        {
            channels[i] = new ChannelProgress();
        }

        return channels;
    }
}
