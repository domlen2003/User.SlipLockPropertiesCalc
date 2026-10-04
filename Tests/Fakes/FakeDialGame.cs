using System;
using System.Collections.Generic;
using DivebombLogistics.Framework;
using DivebombLogistics.SpeedDial;
using DivebombLogistics.SpeedDial.Engine;

namespace DivebombLogistics.Tests.Fakes;

/// <summary>
/// A simulated game for the SpeedDial engine tests. It is both what the dialer reads (<see cref="IDialTelemetry"/>)
/// and what it presses (<see cref="IRoleOutput"/>, the Control Mapper).
/// <para>
/// Every channel holds a value that a press of its increase or decrease role moves by one step (or to the
/// neighbouring entry of an explicit value grid) a number of frames after the press, clamped to [min, max] or
/// wrapping around. Per channel: inverted roles, unbound roles (the press is accepted, the game ignores it),
/// unsupported or unreadable telemetry, a reading override (NaN gaps, glitches), whether the car's maximum is
/// reported, and manual presses or direct value changes by the "driver". <see cref="IsAvailable"/> false simulates a
/// missing Control Mapper (<see cref="Press"/> returns false).
/// </para>
/// <para>
/// Drive it frame by frame: <see cref="Step()"/> advances <see cref="Now"/> by one 60 Hz frame and applies the
/// presses that are due, then the test calls <c>Dialer.Update(game.Now, ...)</c>. Allocation-free in steady state
/// (queue and logs are preallocated), so it can run inside allocation tests.
/// </para>
/// </summary>
internal sealed class FakeDialGame : IDialTelemetry, IRoleOutput
{
    /// <summary>Length of one simulated frame (60 Hz).</summary>
    public const double FrameSeconds = 1.0 / 60.0;

    /// <summary>Default delay between a press and its effect (50 ms).</summary>
    public const int DefaultLatencyFrames = 3;

    /// <summary>Default value of every integer channel.</summary>
    public const double DefaultIntegerValue = 5.0;

    /// <summary>Default maximum of every integer channel.</summary>
    public const double DefaultIntegerMax = 10.0;

    /// <summary>Default brake bias (front %).</summary>
    public const double DefaultBrakeBias = 55.0;

    /// <summary>Default brake bias step (%).</summary>
    public const double DefaultBrakeBiasStep = 0.5;

    /// <summary>Default brake bias range (front %).</summary>
    public const double DefaultBrakeBiasMin = 40.0;

    /// <summary>Default brake bias range (front %).</summary>
    public const double DefaultBrakeBiasMax = 70.0;

    private const int QueueCapacity = 256;
    private const int LogCapacity = 8192;

    /// <summary>Values are rounded like a game would report them (removes float drift such as 54.199999999).</summary>
    private const int ValueDecimals = 6;

    private const double LimitEpsilon = 1e-9;

    private readonly double[] values = new double[DialChannels.Count];
    private readonly double[] steps = new double[DialChannels.Count];
    private readonly double[] mins = new double[DialChannels.Count];
    private readonly double[] maxs = new double[DialChannels.Count];
    private readonly double[][] grids = new double[DialChannels.Count][];
    private readonly int[] latencies = new int[DialChannels.Count];
    private readonly bool[] wraps = new bool[DialChannels.Count];
    private readonly bool[] inverted = new bool[DialChannels.Count];
    private readonly bool[] bound = new bool[DialChannels.Count];
    private readonly bool[] supported = new bool[DialChannels.Count];
    private readonly bool[] readable = new bool[DialChannels.Count];
    private readonly bool[] reportsMax = new bool[DialChannels.Count];
    private readonly bool[] overridden = new bool[DialChannels.Count];
    private readonly double[] overrides = new double[DialChannels.Count];
    private readonly string[] increaseRoles = new string[DialChannels.Count];
    private readonly string[] decreaseRoles = new string[DialChannels.Count];
    private readonly int[] pressCounts = new int[DialChannels.Count];

    private readonly int[] queueFrames = new int[QueueCapacity];
    private readonly int[] queueChannels = new int[QueueCapacity];
    private readonly int[] queueSigns = new int[QueueCapacity];
    private int queueCount;

    private readonly List<string> pressedRoles = new List<string>(LogCapacity);
    private readonly List<double> pressTimes = new List<double>(LogCapacity);
    private readonly List<double> changeTimes = new List<double>(LogCapacity);
    private readonly List<DialChannel> changeChannels = new List<DialChannel>(LogCapacity);

    /// <summary>
    /// A game where every channel is supported, readable, bound to the user's default roles, not inverted, clamping
    /// and with <see cref="DefaultLatencyFrames"/> latency: integer channels at 5 in 0..10 (step 1), brake bias at
    /// 55 % in 40..70 % (step 0.5). The maximum is not reported (like iRacing/ACC) until <see cref="SetReportsMax"/>.
    /// </summary>
    public FakeDialGame()
    {
        for (int i = 0; i < DialChannels.Count; i++)
        {
            var channel = (DialChannel)i;
            bool continuous = DialChannels.Kind(channel) == DialChannelKind.Continuous;
            values[i] = continuous ? DefaultBrakeBias : DefaultIntegerValue;
            steps[i] = continuous ? DefaultBrakeBiasStep : 1.0;
            mins[i] = continuous ? DefaultBrakeBiasMin : 0.0;
            maxs[i] = continuous ? DefaultBrakeBiasMax : DefaultIntegerMax;
            latencies[i] = DefaultLatencyFrames;
            bound[i] = true;
            supported[i] = true;
            readable[i] = true;
            overrides[i] = double.NaN;
            increaseRoles[i] = DialChannels.DefaultIncreaseRole(channel);
            decreaseRoles[i] = DialChannels.DefaultDecreaseRole(channel);
        }
    }

    /// <inheritdoc />
    public string Name => "Fake game";

    /// <summary>False simulates a missing Control Mapper plugin: <see cref="Press"/> returns false.</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>Simulated monotonic time in seconds (advanced by <see cref="Step()"/>).</summary>
    public double Now { get; set; } = 100.0;

    /// <summary>Frames simulated so far.</summary>
    public int Frame { get; private set; }

    /// <summary>Duration passed with the latest accepted press.</summary>
    public int LastPressDurationMs { get; private set; }

    /// <summary>Every role the dialer pressed (accepted presses only), in order.</summary>
    public IReadOnlyList<string> PressedRoles => pressedRoles;

    /// <summary><see cref="Now"/> of every accepted press (parallel to <see cref="PressedRoles"/>).</summary>
    public IReadOnlyList<double> PressTimes => pressTimes;

    /// <summary><see cref="Now"/> of every value change caused by a press (dialer or manual).</summary>
    public IReadOnlyList<double> ChangeTimes => changeTimes;

    /// <summary>The channel of every entry in <see cref="ChangeTimes"/>.</summary>
    public IReadOnlyList<DialChannel> ChangeChannels => changeChannels;

    /// <summary>Accepted presses so far.</summary>
    public int TotalPresses => pressedRoles.Count;

    /// <summary>Press effects that have not landed yet.</summary>
    public int PendingEffects => queueCount;

    // ---------------------------------------------------------------------------------------------------
    // Configuration
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Sets value, step and range of a channel (removes a grid).</summary>
    public FakeDialGame Configure(DialChannel channel, double value, double step, double min, double max)
    {
        int i = Index(channel);
        values[i] = value;
        steps[i] = step;
        mins[i] = min;
        maxs[i] = max;
        grids[i] = null;
        return this;
    }

    /// <summary>Presses move to the neighbouring entry of <paramref name="grid"/> (ascending) instead of by a step; the range becomes the grid's ends.</summary>
    public FakeDialGame SetGrid(DialChannel channel, params double[] grid)
    {
        int i = Index(channel);
        grids[i] = grid;
        mins[i] = grid[0];
        maxs[i] = grid[grid.Length - 1];
        return this;
    }

    /// <summary>Frames between a press and its effect (at least 1).</summary>
    public FakeDialGame SetLatency(DialChannel channel, int frames)
    {
        latencies[Index(channel)] = Math.Max(1, frames);
        return this;
    }

    /// <summary>The increase role lowers the value and the decrease role raises it.</summary>
    public FakeDialGame SetInverted(DialChannel channel, bool value = true)
    {
        inverted[Index(channel)] = value;
        return this;
    }

    /// <summary>False: the game has nothing bound to the channel's roles (presses are accepted and ignored).</summary>
    public FakeDialGame SetBound(DialChannel channel, bool value)
    {
        bound[Index(channel)] = value;
        return this;
    }

    /// <summary>False: the sim does not expose the channel (<see cref="IsSupported"/> false).</summary>
    public FakeDialGame SetSupported(DialChannel channel, bool value)
    {
        supported[Index(channel)] = value;
        return this;
    }

    /// <summary>False: the property is missing right now (<see cref="TryRead"/> false), e.g. in a menu.</summary>
    public FakeDialGame SetReadable(DialChannel channel, bool value)
    {
        readable[Index(channel)] = value;
        return this;
    }

    /// <summary>True: stepping past the maximum goes to the minimum and vice versa (instead of clamping).</summary>
    public FakeDialGame SetWraps(DialChannel channel, bool value = true)
    {
        wraps[Index(channel)] = value;
        return this;
    }

    /// <summary>True: <see cref="TryGetMax"/> reports the channel's maximum (like LMU's <c>mTCMax</c>).</summary>
    public FakeDialGame SetReportsMax(DialChannel channel, bool value = true)
    {
        reportsMax[Index(channel)] = value;
        return this;
    }

    // ---------------------------------------------------------------------------------------------------
    // State and the driver's own actions
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The game's real value of a channel (ignores overrides and readability).</summary>
    public double GetValue(DialChannel channel) => values[Index(channel)];

    /// <summary>The driver (or the game) changes the value directly, visible at once.</summary>
    public void SetValue(DialChannel channel, double value) => values[Index(channel)] = value;

    /// <summary>From now on <see cref="TryRead"/> reports <paramref name="reading"/> (NaN = no value) instead of the real value.</summary>
    public void OverrideReading(DialChannel channel, double reading)
    {
        int i = Index(channel);
        overridden[i] = true;
        overrides[i] = reading;
    }

    /// <summary>Ends <see cref="OverrideReading"/>.</summary>
    public void ClearOverride(DialChannel channel) => overridden[Index(channel)] = false;

    /// <summary>The driver presses the channel's own button; lands after <paramref name="latencyFrames"/> frames. Not logged as a dialer press.</summary>
    public void ManualPress(DialChannel channel, bool increase, int latencyFrames)
    {
        int i = Index(channel);
        Enqueue(i, increase ? 1 : -1, latencyFrames);
    }

    /// <summary>Accepted dialer presses of one channel's roles.</summary>
    public int PressCount(DialChannel channel) => pressCounts[Index(channel)];

    /// <summary>Forgets the press and change logs (counters per channel included).</summary>
    public void ClearLog()
    {
        pressedRoles.Clear();
        pressTimes.Clear();
        changeTimes.Clear();
        changeChannels.Clear();
        Array.Clear(pressCounts, 0, pressCounts.Length);
    }

    /// <summary>Advances one frame: <see cref="Now"/> += <see cref="FrameSeconds"/>, then the presses that are due land (in press order).</summary>
    public void Step()
    {
        Frame++;
        Now += FrameSeconds;
        int kept = 0;
        for (int read = 0; read < queueCount; read++)
        {
            if (queueFrames[read] <= Frame)
            {
                Apply(queueChannels[read], queueSigns[read]);
            }
            else
            {
                queueFrames[kept] = queueFrames[read];
                queueChannels[kept] = queueChannels[read];
                queueSigns[kept] = queueSigns[read];
                kept++;
            }
        }

        queueCount = kept;
    }

    // ---------------------------------------------------------------------------------------------------
    // IDialTelemetry
    // ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool IsSupported(DialChannel channel) => DialChannels.IsValid(channel) && supported[(int)channel];

    /// <inheritdoc />
    public bool TryRead(DialChannel channel, out double value)
    {
        if (!IsSupported(channel) || !readable[(int)channel])
        {
            value = double.NaN;
            return false;
        }

        int i = (int)channel;
        value = overridden[i] ? overrides[i] : values[i];
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            value = double.NaN;
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryGetMax(DialChannel channel, out double max)
    {
        if (IsSupported(channel) && reportsMax[(int)channel])
        {
            max = maxs[(int)channel];
            return true;
        }

        max = double.NaN;
        return false;
    }

    /// <inheritdoc />
    public string Describe() => "Fake game telemetry";

    // ---------------------------------------------------------------------------------------------------
    // IRoleOutput
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Accepts the press (logged) and schedules its effect when a channel of the game reacts to the role.</summary>
    public bool Press(string role, int durationMs)
    {
        if (!IsAvailable || string.IsNullOrEmpty(role))
        {
            return false;
        }

        pressedRoles.Add(role);
        pressTimes.Add(Now);
        LastPressDurationMs = durationMs;
        for (int i = 0; i < DialChannels.Count; i++)
        {
            int sign = string.Equals(role, increaseRoles[i], StringComparison.Ordinal) ? 1
                : string.Equals(role, decreaseRoles[i], StringComparison.Ordinal) ? -1 : 0;
            if (sign == 0)
            {
                continue;
            }

            pressCounts[i]++;
            if (bound[i])
            {
                Enqueue(i, inverted[i] ? -sign : sign, latencies[i]);
            }

            break;
        }

        return true; // the Control Mapper presses any role; whether the game reacts is another matter
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetButtonRoles() => Array.Empty<string>();

    // ---------------------------------------------------------------------------------------------------

    private static int Index(DialChannel channel)
    {
        if (!DialChannels.IsValid(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        return (int)channel;
    }

    private void Enqueue(int channel, int sign, int latencyFrames)
    {
        if (queueCount == QueueCapacity)
        {
            throw new InvalidOperationException("FakeDialGame press queue overflow");
        }

        queueFrames[queueCount] = Frame + Math.Max(1, latencyFrames);
        queueChannels[queueCount] = channel;
        queueSigns[queueCount] = sign;
        queueCount++;
    }

    private void Apply(int i, int sign)
    {
        double current = values[i];
        double next;
        double[] grid = grids[i];
        if (grid != null)
        {
            next = GridNeighbour(grid, current, sign, wraps[i]);
        }
        else
        {
            next = current + (sign * steps[i]);
            if (next > maxs[i] + LimitEpsilon)
            {
                next = wraps[i] ? mins[i] : maxs[i];
            }
            else if (next < mins[i] - LimitEpsilon)
            {
                next = wraps[i] ? maxs[i] : mins[i];
            }
        }

        next = Math.Round(next, ValueDecimals);
        if (next != current)
        {
            values[i] = next;
            changeTimes.Add(Now);
            changeChannels.Add((DialChannel)i);
        }
    }

    private static double GridNeighbour(double[] grid, double current, int sign, bool wrap)
    {
        int nearest = 0;
        double best = double.PositiveInfinity;
        for (int i = 0; i < grid.Length; i++)
        {
            double distance = Math.Abs(grid[i] - current);
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        int next = nearest + sign;
        if (next >= grid.Length)
        {
            next = wrap ? 0 : grid.Length - 1;
        }
        else if (next < 0)
        {
            next = wrap ? grid.Length - 1 : 0;
        }

        return grid[next];
    }
}
