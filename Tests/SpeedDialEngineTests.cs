using System;
using DivebombLogistics.Core;
using DivebombLogistics.SpeedDial;
using DivebombLogistics.SpeedDial.Engine;
using DivebombLogistics.SpeedDial.Model;
using DivebombLogistics.Tests.Fakes;

namespace DivebombLogistics.Tests;

/// <summary>
/// The SpeedDial dial engine (<see cref="Dialer"/>) against a simulated game (<see cref="FakeDialGame"/>): reaching
/// integer and continuous targets with minimal presses, direction and step learning, every failure result, the
/// final-state rules, the gate, replacing and cancelling jobs, press timing, robustness against manual presses and
/// bad telemetry, and the allocation-free hot path.
/// </summary>
internal static class SpeedDialEngineTests
{
    /// <summary>Upper bound for any job in these tests (about 5.5 simulated minutes at 60 Hz).</summary>
    private const int MaxFrames = 20000;

    private const double TimeEpsilon = 1e-9;
    private const double ValueEpsilon = 1e-9;
    private const string TcUp = "TractionControl+";
    private const string TcDown = "TractionControl-";
    private const string AbsUp = "ABS+";
    private const string BbFront = "BrakeBalanceFront";
    private const string BbRear = "BrakeBalanceRear";

    [Test]
    public static void Dialer_RequiresARoleOutput()
    {
        Assert.Throws<ArgumentNullException>(() => new Dialer(null, NullLog.Instance));
        var dialer = new Dialer(new FakeDialGame(), null);
        Assert.Equal(DialState.Idle, dialer.Status.State);
        Assert.False(dialer.IsBusy, "idle");
        Assert.False(dialer.LearningChanged, "nothing learned yet");
    }

    [Test]
    public static void Dialer_TcDialsUpWithOnePressPerLevel()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10);
        bench.Start("Up", (DialChannel.Tc1, 7));
        Assert.Equal(DialState.Running, bench.Status.State);
        Assert.Equal(DialerMessages.Starting, bench.Status.Message);
        Assert.True(ReferenceEquals(bench.Request.Label, bench.Status.Label), "label copied by reference");
        Assert.Equal(0, bench.Game.TotalPresses, "Start never presses");

        bench.RunToEnd();

        ChannelProgress tc = bench.Progress(DialChannel.Tc1);
        Assert.Equal(DialState.Completed, bench.Status.State);
        Assert.Equal(DialerMessages.Done, bench.Status.Message);
        Assert.Equal(ChannelResult.Reached, tc.Result);
        Assert.Equal(7.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(4, tc.Presses, "one press per level");
        Assert.Equal(4, bench.Game.TotalPresses);
        Assert.Equal(4, CountRole(bench.Game, TcUp));
        Assert.Equal(DialTiming.DefaultPressMs, bench.Game.LastPressDurationMs, "press duration from the settings");
        Assert.Equal(3.0, tc.Start);
        Assert.Equal(7.0, tc.Target);
        Assert.Equal(7.0, tc.Current);
        Assert.True(bench.Status.CurrentChannel == null, "no current channel after the end");
        Assert.True(bench.Status.FinishedAt > bench.Status.StartedAt, "finish time set");
        Assert.Equal(ChannelResult.Skipped, bench.Progress(DialChannel.Abs).Result, "untargeted channel");

        ChannelLearning learning = bench.Learning(DialChannel.Tc1);
        Assert.True(bench.Dialer.LearningChanged, "learning reported");
        Assert.Near(1.0, learning.Step, ValueEpsilon, "step");
        Assert.True(learning.DirectionConfirmed, "direction confirmed");
        Assert.Equal(ChannelLearning.IncreaseRaises, learning.Direction);
        Assert.Equal(4.0, learning.ObservedMin);
        Assert.Equal(7.0, learning.ObservedMax);
        bench.Dialer.ClearLearningChanged();
        Assert.False(bench.Dialer.LearningChanged, "cleared");
    }

    [Test]
    public static void Dialer_TcDialsDown()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 8, 1, 0, 10);
        bench.Start("Down", (DialChannel.Tc1, 2));
        bench.RunToEnd();

        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(2.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(6, bench.Game.TotalPresses);
        Assert.Equal(6, CountRole(bench.Game, TcDown));
    }

    [Test]
    public static void Dialer_AlreadyAtTargetPressesNothing()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Abs, 4, 1, 0, 10);
        bench.Start("Same", (DialChannel.Abs, 4));
        bench.Frame();

        Assert.Equal(DialState.Completed, bench.Status.State, "finished in the first frame");
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Abs).Result);
        Assert.Equal(0, bench.Game.TotalPresses);
        Assert.False(bench.Dialer.LearningChanged, "no press, nothing learned");
    }

    [Test]
    public static void Dialer_BrakeBiasLearnsHalfPercentSteps()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.BrakeBias, 54.0, 0.5, 40, 70);
        bench.Start("BB", (DialChannel.BrakeBias, 56.5));
        bench.RunToEnd();

        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.BrakeBias).Result);
        Assert.Near(56.5, bench.Game.GetValue(DialChannel.BrakeBias), ValueEpsilon);
        Assert.Equal(5, CountRole(bench.Game, BbFront));
        Assert.Near(0.5, bench.Learning(DialChannel.BrakeBias).Step, 1e-6, "learned step");

        // With the step known, a target between two positions is reached within half a step (DialChannels.Matches).
        bench.Game.ClearLog();
        bench.Start("Fraction", (DialChannel.BrakeBias, 56.3));
        bench.RunToEnd();
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.BrakeBias).Result);
        Assert.Equal(0, bench.Game.TotalPresses, "56.5 is the closest position to 56.3");

        bench.Start("Back", (DialChannel.BrakeBias, 55.2));
        bench.RunToEnd();
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.BrakeBias).Result);
        Assert.Near(55.0, bench.Game.GetValue(DialChannel.BrakeBias), ValueEpsilon, "closest position to 55.2");
        Assert.Equal(3, CountRole(bench.Game, BbRear));
    }

    [Test]
    public static void Dialer_BrakeBiasFifthPercentSteps()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.BrakeBias, 54.0, 0.2, 40, 70);
        bench.Start("BB", (DialChannel.BrakeBias, 53.0));
        bench.RunToEnd();

        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.BrakeBias).Result);
        Assert.Near(53.0, bench.Game.GetValue(DialChannel.BrakeBias), ValueEpsilon);
        Assert.Equal(5, CountRole(bench.Game, BbRear));
        Assert.Equal(5, bench.Game.TotalPresses);
        Assert.Near(0.2, bench.Learning(DialChannel.BrakeBias).Step, 1e-6, "learned step");
    }

    [Test]
    public static void Dialer_ReversedDirectionIsLearnedOnceAndPersisted()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10).SetInverted(DialChannel.Tc1);
        bench.Start("Up", (DialChannel.Tc1, 6));
        bench.RunToEnd();

        ChannelLearning learning = bench.Learning(DialChannel.Tc1);
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(6.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(5, bench.Game.TotalPresses, "one wasted press, then one per level");
        Assert.Equal(TcUp, bench.Game.PressedRoles[0]);
        Assert.Equal(4, CountRole(bench.Game, TcDown), "Decrease raises the value in this game");
        Assert.Equal(ChannelLearning.IncreaseLowers, learning.Direction);
        Assert.True(learning.DirectionConfirmed, "flip confirms");
        Assert.True(bench.Dialer.LearningChanged, "learning reported");

        // The next job uses the learned direction: no wasted press, and nothing new to save.
        bench.Dialer.ClearLearningChanged();
        bench.Game.ClearLog();
        bench.Start("Down", (DialChannel.Tc1, 4));
        bench.RunToEnd();
        Assert.Equal(4.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(2, bench.Game.TotalPresses);
        Assert.Equal(2, CountRole(bench.Game, TcUp), "Increase lowers the value");
        Assert.False(bench.Dialer.LearningChanged, "stable learning is not reported again");
    }

    [Test]
    public static void Dialer_ReversedBindingAtALimitIsFoundByTryingTheOtherRole()
    {
        // TC at 0, Increase actually lowers: the first presses push into the minimum and nothing moves.
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 0, 1, 0, 10).SetInverted(DialChannel.Tc1);
        bench.Start("FromZero", (DialChannel.Tc1, 3));
        bench.RunToEnd();

        ChannelLearning learning = bench.Learning(DialChannel.Tc1);
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(3.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(DialTiming.DefaultMaxStallPresses, CountRole(bench.Game, TcUp), "stalls against the minimum");
        Assert.Equal(3, CountRole(bench.Game, TcDown), "then the other role, one press per level");
        Assert.Equal(ChannelLearning.IncreaseLowers, learning.Direction);
        Assert.True(learning.DirectionConfirmed, "confirmed by the moves");
        Assert.True(bench.Dialer.LearningChanged, "reported");

        // The same at the reported maximum while lowering.
        var top = new Bench();
        top.Game.Configure(DialChannel.Abs, 10, 1, 0, 10).SetReportsMax(DialChannel.Abs).SetInverted(DialChannel.Abs);
        top.Start("FromTop", (DialChannel.Abs, 7));
        top.RunToEnd();
        Assert.Equal(ChannelResult.Reached, top.Progress(DialChannel.Abs).Result);
        Assert.Equal(7.0, top.Game.GetValue(DialChannel.Abs));
        Assert.Equal(ChannelLearning.IncreaseLowers, top.Learning(DialChannel.Abs).Direction);

        // A dead binding at the minimum: both roles tried, nothing learned.
        var dead = new Bench();
        dead.Game.Configure(DialChannel.Tc1, 0, 1, 0, 10).SetBound(DialChannel.Tc1, false);
        dead.Start("Dead", (DialChannel.Tc1, 3));
        dead.RunToEnd();
        Assert.Equal(ChannelResult.NoResponse, dead.Progress(DialChannel.Tc1).Result);
        Assert.Equal(2 * DialTiming.DefaultMaxStallPresses, dead.Game.TotalPresses, "each role MaxStallPresses times");
        Assert.Equal(ChannelLearning.IncreaseRaises, dead.Learning(DialChannel.Tc1).Direction, "trial undone");
        Assert.False(dead.Learning(DialChannel.Tc1).DirectionConfirmed, "still unproven");

        // A proven direction is trusted: a stall at the minimum is then a dead binding, not a reversed one.
        var proven = new Bench();
        proven.Game.Configure(DialChannel.Tc1, 0, 1, 0, 10).SetBound(DialChannel.Tc1, false);
        proven.Learning(DialChannel.Tc1).DirectionConfirmed = true;
        proven.Start("Proven", (DialChannel.Tc1, 3));
        proven.RunToEnd();
        Assert.Equal(ChannelResult.NoResponse, proven.Progress(DialChannel.Tc1).Result);
        Assert.Equal(DialTiming.DefaultMaxStallPresses, proven.Game.TotalPresses, "no trial with a proven direction");
    }

    [Test]
    public static void Dialer_SecondWrongMoveIsDirectionErrorAndRestoresLearning()
    {
        var bench = new Bench();
        bench.Settings.GetBinding(DialChannel.Tc1).IncreaseRole = TcDown; // both roles lower the value
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10).Configure(DialChannel.Abs, 5, 1, 0, 10);
        bench.Start("Broken", (DialChannel.Tc1, 6), (DialChannel.Abs, 6));
        bench.RunToEnd();

        ChannelLearning learning = bench.Learning(DialChannel.Tc1);
        Assert.Equal(ChannelResult.DirectionError, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(2, bench.Progress(DialChannel.Tc1).Presses, "stops after the second wrong move");
        Assert.Equal(1.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(ChannelLearning.IncreaseRaises, learning.Direction, "direction restored");
        Assert.False(learning.DirectionConfirmed, "restored to unconfirmed");
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Abs).Result, "the job goes on");
        Assert.Equal(DialState.Partial, bench.Status.State);
        Assert.Equal(DialerMessages.Partial(DialChannel.Tc1, ChannelResult.DirectionError), bench.Status.Message);
        Assert.Equal("Partly done: TC moved the wrong way", bench.Status.Message);
    }

    [Test]
    public static void Dialer_UnboundRoleEndsWithNoResponse()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10).SetBound(DialChannel.Tc1, false);
        double start = bench.Game.Now;
        bench.Start("Unbound", (DialChannel.Tc1, 7));
        bench.RunToEnd();

        ChannelProgress tc = bench.Progress(DialChannel.Tc1);
        Assert.Equal(ChannelResult.NoResponse, tc.Result);
        Assert.Equal(DialTiming.DefaultMaxStallPresses, tc.Presses, "MaxStallPresses presses");
        Assert.Equal(3.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(DialState.Failed, bench.Status.State);
        Assert.Equal("Failed: TC no response", bench.Status.Message);
        double expected = DialTiming.DefaultMaxStallPresses * DialTiming.DefaultConfirmTimeoutMs / 1000.0;
        Assert.True(bench.Game.Now - start >= expected, "each press waited the confirmation timeout");
        Assert.False(bench.Dialer.LearningChanged, "nothing learned from silence");
        Assert.Equal(0.0, bench.Learning(DialChannel.Tc1).Step);
    }

    [Test]
    public static void Dialer_ControlMapperUnavailableIsNoBinding()
    {
        var bench = new Bench();
        bench.Game.IsAvailable = false;
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10).Configure(DialChannel.Abs, 5, 1, 0, 10);
        bench.Start("NoMapper", (DialChannel.Tc1, 7), (DialChannel.Abs, 5));
        bench.RunToEnd();

        Assert.Equal(ChannelResult.NoBinding, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(0, bench.Progress(DialChannel.Tc1).Presses, "a refused press is not counted");
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Abs).Result, "already there: no press needed");
        Assert.Equal(DialState.Partial, bench.Status.State);

        bench.Start("NoMapper2", (DialChannel.Tc1, 7));
        bench.RunToEnd();
        Assert.Equal(DialState.Failed, bench.Status.State);
        Assert.Equal("Failed: TC no binding", bench.Status.Message);
        Assert.Equal(0, bench.Game.TotalPresses);
    }

    [Test]
    public static void Dialer_EmptyRoleIsNoBindingAndDisabledBindingIsSkipped()
    {
        var bench = new Bench();
        bench.Settings.GetBinding(DialChannel.Tc1).IncreaseRole = string.Empty;
        bench.Settings.GetBinding(DialChannel.Abs).Enabled = false;
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10)
            .Configure(DialChannel.Tc2, 5, 1, 0, 10)
            .Configure(DialChannel.Abs, 5, 1, 0, 10);
        bench.Start("Bindings", (DialChannel.Tc1, 7), (DialChannel.Tc2, 3), (DialChannel.Abs, 8));
        bench.RunToEnd();

        Assert.Equal(ChannelResult.NoBinding, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(0, bench.Progress(DialChannel.Tc1).Presses);
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc2).Result);
        Assert.Equal(ChannelResult.Skipped, bench.Progress(DialChannel.Abs).Result, "disabled binding");
        Assert.Equal(0, bench.Game.PressCount(DialChannel.Abs));
        Assert.Equal(5.0, bench.Game.GetValue(DialChannel.Abs), "disabled channel untouched");
        Assert.Equal(DialState.Partial, bench.Status.State);

        // Only the role actually needed must be bound.
        bench.Start("DownOnly", (DialChannel.Tc1, 2));
        bench.RunToEnd();
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(2.0, bench.Game.GetValue(DialChannel.Tc1));
    }

    [Test]
    public static void Dialer_DisablingABindingMidDialStopsPressing()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 0, 1, 0, 10);
        bench.Start("Long", (DialChannel.Tc1, 9));
        bench.RunUntilPresses(2);
        bench.Settings.GetBinding(DialChannel.Tc1).Enabled = false;
        bench.RunToEnd();

        Assert.Equal(ChannelResult.Skipped, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(2, bench.Game.TotalPresses, "no press after the binding was disabled");
    }

    [Test]
    public static void Dialer_RunawayJumpsStopWithDirectionError()
    {
        // A reversed game that moves three levels per press: every press jumps the wrong way, from a new value.
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 15, 3, 0, 30).SetInverted(DialChannel.Tc1);
        bench.Start("Runaway", (DialChannel.Tc1, 20));
        bench.RunToEnd();

        Assert.Equal(ChannelResult.DirectionError, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(3, bench.Game.TotalPresses, "stopped after three wrong-way jumps");
        Assert.Equal(6.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(ChannelLearning.IncreaseRaises, bench.Learning(DialChannel.Tc1).Direction, "jumps teach nothing");
    }

    [Test]
    public static void Dialer_SurvivesMissingSettingsAndBadTime()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10);
        bench.Start("Defaults", (DialChannel.Tc1, 5));
        int version = bench.Status.Version;
        bench.Dialer.Update(double.NaN, true, bench.Game, bench.Settings, bench.Car);
        bench.Dialer.Update(double.PositiveInfinity, true, bench.Game, bench.Settings, bench.Car);
        Assert.Equal(version, bench.Status.Version, "a non-finite time skips the frame");
        Assert.Equal(0, bench.Game.TotalPresses);

        for (int i = 0; i < MaxFrames && bench.Dialer.IsBusy; i++)
        {
            bench.Game.Step();
            bench.Dialer.Update(bench.Game.Now, true, bench.Game, null, bench.Car);
        }

        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result, "default settings");
        Assert.Equal(5.0, bench.Game.GetValue(DialChannel.Tc1));

        bench.Settings.Timing = null;
        bench.Start("No timing", (DialChannel.Tc1, 3));
        bench.RunToEnd();
        Assert.Equal(3.0, bench.Game.GetValue(DialChannel.Tc1), "default timing");
        Assert.Equal(DialTiming.DefaultPressMs, bench.Game.LastPressDurationMs);
    }

    [Test]
    public static void Dialer_StallAtUnreportedMaximumIsLimitReached()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 8, 1, 0, 10);
        bench.Start("TooHigh", (DialChannel.Tc1, 12));
        bench.RunToEnd();

        ChannelProgress tc = bench.Progress(DialChannel.Tc1);
        Assert.Equal(ChannelResult.LimitReached, tc.Result);
        Assert.Equal(10.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(2 + DialTiming.DefaultMaxStallPresses, tc.Presses, "two moves, then the stalls");
        Assert.Equal(12.0, tc.Target, "no known maximum: target unchanged");
        Assert.Equal("Failed: TC at its limit", bench.Status.Message);
    }

    [Test]
    public static void Dialer_KnownMaximumClampsTheTarget()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 8, 1, 0, 10).SetReportsMax(DialChannel.Tc1);
        bench.Start("TooHigh", (DialChannel.Tc1, 12));
        bench.RunToEnd();

        ChannelProgress tc = bench.Progress(DialChannel.Tc1);
        Assert.Equal(ChannelResult.Reached, tc.Result, "the clamped target counts as reached");
        Assert.Equal(10.0, tc.Target, "clamped to the car's maximum");
        Assert.Equal(10.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(2, tc.Presses, "no stall presses at the maximum");
        Assert.Equal(DialState.Completed, bench.Status.State);
    }

    [Test]
    public static void Dialer_LimitReachedNeedsEvidenceOfALimit()
    {
        // The car's lowest TC is 1. Starting there, nothing ever moves: indistinguishable from a dead binding.
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 1, 1, 1, 10);
        bench.Start("Zero", (DialChannel.Tc1, 0));
        bench.RunToEnd();
        Assert.Equal(ChannelResult.NoResponse, bench.Progress(DialChannel.Tc1).Result, "never seen moving");

        // A learned range is no proof of a limit either (it only shows where the value has been).
        ChannelLearning learning = bench.Learning(DialChannel.Tc1);
        learning.DirectionConfirmed = true;
        learning.Step = 1;
        learning.Observe(1);
        learning.Observe(6);
        bench.Start("Zero2", (DialChannel.Tc1, 0));
        bench.RunToEnd();
        Assert.Equal(ChannelResult.NoResponse, bench.Progress(DialChannel.Tc1).Result, "learned range");

        // Moving down and then stalling in the same run is a limit.
        bench.Game.SetValue(DialChannel.Tc1, 3);
        bench.Start("Zero3", (DialChannel.Tc1, 0));
        bench.RunToEnd();
        Assert.Equal(ChannelResult.LimitReached, bench.Progress(DialChannel.Tc1).Result, "moved, then stalled");
        Assert.Equal(1.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(2 + DialTiming.DefaultMaxStallPresses, bench.Progress(DialChannel.Tc1).Presses);
    }

    [Test]
    public static void Dialer_IntegerTargetBetweenStepsIsClosestPossible()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 0, 2, 0, 10); // this game moves TC in steps of 2
        bench.Start("Odd", (DialChannel.Tc1, 3));
        bench.RunToEnd();

        ChannelProgress tc = bench.Progress(DialChannel.Tc1);
        Assert.Equal(ChannelResult.ClosestPossible, tc.Result);
        Assert.Equal(2, tc.Presses, "stops at the first value past the target");
        Assert.Equal(1.0, Math.Abs(bench.Game.GetValue(DialChannel.Tc1) - 3.0), "one level off is the closest possible");
        Assert.Equal(DialState.Completed, bench.Status.State, "ClosestPossible counts as success");
    }

    [Test]
    public static void Dialer_UnreachableFractionalTargetIsClosestPossible()
    {
        var bench = new Bench();
        bench.Game.SetGrid(DialChannel.BrakeBias, 53.0, 53.5, 54.5, 55.0).SetValue(DialChannel.BrakeBias, 53.0);
        bench.Start("Gap", (DialChannel.BrakeBias, 54.0)); // 54.0 does not exist on this car
        bench.RunToEnd();

        ChannelProgress bb = bench.Progress(DialChannel.BrakeBias);
        Assert.Equal(ChannelResult.ClosestPossible, bb.Result);
        Assert.Near(0.5, Math.Abs(bench.Game.GetValue(DialChannel.BrakeBias) - 54.0), ValueEpsilon, "a closest position");
        Assert.True(bb.Presses <= 3, "bounded: " + bb.Presses + " presses");
        Assert.Equal(DialState.Completed, bench.Status.State);
    }

    [Test]
    public static void Dialer_OvershootPressesBackToTheCloserValue()
    {
        var bench = new Bench();
        bench.Game.SetGrid(DialChannel.BrakeBias, 53.0, 53.4, 54.0, 54.4).SetValue(DialChannel.BrakeBias, 53.0);
        bench.Start("Between", (DialChannel.BrakeBias, 53.65));
        bench.RunToEnd();

        ChannelProgress bb = bench.Progress(DialChannel.BrakeBias);
        Assert.Equal(ChannelResult.ClosestPossible, bb.Result);
        Assert.Near(53.4, bench.Game.GetValue(DialChannel.BrakeBias), ValueEpsilon, "53.4 is closer than 54.0");
        Assert.Equal(3, bb.Presses, "up, up past the target, one back");
    }

    [Test]
    public static void Dialer_MultiChannelPresetRunsSequentiallyInSettingsOrder()
    {
        var bench = new Bench();
        bench.Settings.GetBinding(DialChannel.Tc3).Enabled = false;
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10)
            .Configure(DialChannel.Tc2, 5, 1, 0, 10)
            .Configure(DialChannel.Tc3, 4, 1, 0, 10)
            .Configure(DialChannel.Abs, 2, 1, 0, 10)
            .Configure(DialChannel.BrakeBias, 50.0, 0.5, 40, 70);
        bench.Start("Race", (DialChannel.Tc1, 5), (DialChannel.Tc3, 6), (DialChannel.Abs, 4), (DialChannel.BrakeBias, 51.0));
        bench.RunToEnd();

        Assert.Equal(DialState.Completed, bench.Status.State);
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(ChannelResult.Skipped, bench.Progress(DialChannel.Tc2).Result, "no target");
        Assert.Equal(ChannelResult.Skipped, bench.Progress(DialChannel.Tc3).Result, "binding disabled");
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Abs).Result);
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.BrakeBias).Result);
        Assert.Equal(4.0, bench.Game.GetValue(DialChannel.Tc3));
        Assert.Equal(5.0, bench.Game.GetValue(DialChannel.Tc2));
        Assert.Equal(6, bench.Game.TotalPresses, "two presses per dialed channel");
        AssertRoles(bench.Game, TcUp, AbsUp, BbFront);
        DialChannel[] changeOrder = { DialChannel.Tc1, DialChannel.Tc1, DialChannel.Abs, DialChannel.Abs, DialChannel.BrakeBias, DialChannel.BrakeBias };
        Assert.Equal(changeOrder.Length, bench.Game.ChangeChannels.Count);
        for (int i = 0; i < changeOrder.Length; i++)
        {
            Assert.Equal(changeOrder[i], bench.Game.ChangeChannels[i], "channel " + i + " changed in order (no interleaving)");
        }

        bench.Settings.ChannelOrder = new[] { DialChannels.IdBrakeBias, DialChannels.IdAbs };
        bench.Settings.Normalize();
        bench.Game.ClearLog();
        bench.Start("Back", (DialChannel.Tc1, 4), (DialChannel.Abs, 3), (DialChannel.BrakeBias, 50.5));
        bench.RunToEnd();
        Assert.Equal(DialState.Completed, bench.Status.State);
        AssertRoles(bench.Game, BbRear, "ABS-", TcDown);
    }

    [Test]
    public static void Dialer_ManualInterferenceStillConverges()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 2, 1, 0, 10);
        bench.Start("Up", (DialChannel.Tc1, 8));

        // The driver dials TC down by two while the dialer waits between presses.
        bench.RunUntilChanges(3);
        Assert.Equal(5.0, bench.Game.GetValue(DialChannel.Tc1));
        bench.Game.SetValue(DialChannel.Tc1, 3);

        // The driver presses TC- right after the dialer's next press: the contrary step lands first.
        bench.RunUntilPresses(4);
        bench.Game.ManualPress(DialChannel.Tc1, increase: false, latencyFrames: 1);
        bench.RunToEnd();

        ChannelLearning learning = bench.Learning(DialChannel.Tc1);
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(8.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(ChannelLearning.IncreaseRaises, learning.Direction, "a manual press is not a reversed direction");
        Assert.True(learning.DirectionConfirmed, "still confirmed");
        Assert.Equal(9, bench.Game.TotalPresses, "3 + the absorbed press + 5 from 3 to 8");
        Assert.Equal(9, CountRole(bench.Game, TcUp), "never pressed the wrong way");
    }

    [Test]
    public static void Dialer_ManualPressDuringTheFirstPressNeverPersistsAFlip()
    {
        // TC at 1, target 3, direction unproven. The game drops the dialer's first press and the driver taps TC- at the
        // same moment: TC drops to the minimum. The tentative flip then stalls there and must be undone.
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 1, 1, 0, 10).SetBound(DialChannel.Tc1, false);
        bench.Start("Up", (DialChannel.Tc1, 3));
        bench.RunUntilPresses(1);
        bench.Game.SetBound(DialChannel.Tc1, true);
        bench.Game.ManualPress(DialChannel.Tc1, increase: false, latencyFrames: 1);
        bench.RunToEnd();

        ChannelLearning learning = bench.Learning(DialChannel.Tc1);
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result, "dials on with the start direction");
        Assert.Equal(3.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(ChannelLearning.IncreaseRaises, learning.Direction, "flip undone");
        Assert.True(learning.DirectionConfirmed, "confirmed by the presses that worked");

        // The next job raises from 0 without a single wrong press.
        bench.Game.SetValue(DialChannel.Tc1, 0);
        bench.Game.ClearLog();
        bench.Start("Again", (DialChannel.Tc1, 2));
        bench.RunToEnd();
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(2, CountRole(bench.Game, TcUp));
        Assert.Equal(2, bench.Game.TotalPresses);
    }

    [Test]
    public static void Dialer_ConfirmedDirectionIgnoresAFirstContraryStep()
    {
        // TC 3, target 5, direction proven earlier. The first press is dropped and the driver taps TC- (TC 2): the
        // dialer must not flip and press TC- itself (that would leave the car two steps off).
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10).SetBound(DialChannel.Tc1, false);
        bench.Learning(DialChannel.Tc1).DirectionConfirmed = true;
        bench.Start("Up", (DialChannel.Tc1, 5));
        bench.RunUntilPresses(1);
        bench.Game.SetBound(DialChannel.Tc1, true);
        bench.Game.ManualPress(DialChannel.Tc1, increase: false, latencyFrames: 1);
        bench.RunToEnd();

        ChannelLearning learning = bench.Learning(DialChannel.Tc1);
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(5.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(0, CountRole(bench.Game, TcDown), "never pressed the wrong way");
        Assert.Equal(ChannelLearning.IncreaseRaises, learning.Direction);
        Assert.True(learning.DirectionConfirmed);
    }

    [Test]
    public static void Dialer_UnprovenFlipIsUndoneWhenTheJobEnds()
    {
        // Flip tentatively (dropped press + manual TC-), then cancel before the flipped role proved anything.
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10).SetBound(DialChannel.Tc1, false);
        bench.Start("Up", (DialChannel.Tc1, 6));
        bench.RunUntilPresses(1);
        bench.Game.ManualPress(DialChannel.Tc1, increase: false, latencyFrames: 1);
        bench.RunUntilChanges(1);
        bench.Frame();
        Assert.Equal(ChannelLearning.IncreaseLowers, bench.Learning(DialChannel.Tc1).Direction, "tentative flip");
        Assert.False(bench.Learning(DialChannel.Tc1).DirectionConfirmed, "a single step never confirms");
        bench.Dialer.Cancel(null);
        Assert.Equal(ChannelLearning.IncreaseRaises, bench.Learning(DialChannel.Tc1).Direction, "undone on cancel");
        Assert.False(bench.Learning(DialChannel.Tc1).DirectionConfirmed);

        // The same when a new job replaces the running one.
        bench.Game.SetValue(DialChannel.Tc1, 3);
        bench.Start("Up2", (DialChannel.Tc1, 6));
        bench.RunUntilPresses(2);
        bench.Game.ManualPress(DialChannel.Tc1, increase: false, latencyFrames: 1);
        bench.RunUntilChanges(2);
        bench.Frame();
        Assert.Equal(ChannelLearning.IncreaseLowers, bench.Learning(DialChannel.Tc1).Direction, "tentative flip 2");
        bench.Start("Other", (DialChannel.Abs, 6));
        Assert.Equal(ChannelLearning.IncreaseRaises, bench.Learning(DialChannel.Tc1).Direction, "undone on replace");
    }

    [Test]
    public static void Dialer_TelemetryGapsAndGlitchesAreTolerated()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.BrakeBias, 54.0, 0.5, 40, 70);
        bench.Start("Bumpy", (DialChannel.BrakeBias, 57.0));

        // A 20-frame telemetry gap while the second press lands.
        bench.RunUntilPresses(2);
        bench.Game.OverrideReading(DialChannel.BrakeBias, double.NaN);
        bench.Frames(20);
        Assert.True(bench.Dialer.IsBusy, "still dialing");
        bench.Game.ClearOverride(DialChannel.BrakeBias);

        // A one-frame glitch to 0 % while the fourth press is pending.
        bench.RunUntilPresses(4);
        bench.Game.OverrideReading(DialChannel.BrakeBias, 0.0);
        bench.Frame();
        bench.Game.ClearOverride(DialChannel.BrakeBias);
        bench.RunToEnd();

        ChannelLearning learning = bench.Learning(DialChannel.BrakeBias);
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.BrakeBias).Result);
        Assert.Near(57.0, bench.Game.GetValue(DialChannel.BrakeBias), ValueEpsilon);
        Assert.Equal(6, bench.Game.TotalPresses, "no extra press");
        Assert.Equal(ChannelLearning.IncreaseRaises, learning.Direction, "a glitch is not a reversed direction");
        Assert.Near(0.5, learning.Step, 1e-6, "step not polluted");
        Assert.Near(54.5, learning.ObservedMin, ValueEpsilon, "range not polluted by the glitch");
    }

    [Test]
    public static void Dialer_MissingTelemetryEndsWithNoTelemetry()
    {
        var bench = new Bench();
        bench.Game.SetSupported(DialChannel.Tc3, false);
        bench.Start("Unsupported", (DialChannel.Tc3, 4));
        bench.Frame();
        Assert.Equal(ChannelResult.NoTelemetry, bench.Progress(DialChannel.Tc3).Result, "unsupported: at once");
        Assert.Equal(DialState.Failed, bench.Status.State);
        Assert.Equal("Failed: TC3 (Slip) no telemetry", bench.Status.Message);

        bench.Game.SetReadable(DialChannel.Tc2, false);
        bench.Start("Missing", (DialChannel.Tc2, 3));
        bench.Frames(10);
        Assert.True(bench.Dialer.IsBusy, "a missing value is waited for");
        bench.RunToEnd();
        Assert.Equal(ChannelResult.NoTelemetry, bench.Progress(DialChannel.Tc2).Result, "missing for a confirmation timeout");

        bench.Game.OverrideReading(DialChannel.Abs, double.NaN);
        bench.Start("NaN", (DialChannel.Abs, 3));
        bench.RunToEnd();
        Assert.Equal(ChannelResult.NoTelemetry, bench.Progress(DialChannel.Abs).Result, "NaN");

        bench.Telemetry = null;
        bench.Start("NoSource", (DialChannel.Tc1, 3), (DialChannel.BrakeBias, 50));
        bench.Frame();
        Assert.Equal(ChannelResult.NoTelemetry, bench.Progress(DialChannel.Tc1).Result, "null telemetry");
        Assert.Equal(ChannelResult.NoTelemetry, bench.Progress(DialChannel.BrakeBias).Result, "null telemetry");
        Assert.Equal(DialState.Failed, bench.Status.State);
        Assert.Equal(0, bench.Game.TotalPresses, "never pressed blind");
    }

    [Test]
    public static void Dialer_GatePausesAndResumes()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 2, 1, 0, 10);
        bench.Start("Pause", (DialChannel.Tc1, 6));
        bench.RunUntilPresses(1);

        bench.Gate = false;
        bench.Frame();
        Assert.Equal(DialState.Paused, bench.Status.State);
        Assert.Equal(DialerMessages.Paused, bench.Status.Message);
        Assert.True(bench.Status.CurrentChannel == DialChannel.Tc1, "current channel kept while paused");
        Assert.True(bench.Dialer.IsBusy, "paused is busy");
        bench.Frames(120);
        Assert.Equal(DialState.Paused, bench.Status.State);
        Assert.Equal(1, bench.Game.TotalPresses, "no presses while the gate is closed");
        Assert.Equal(3.0, bench.Game.GetValue(DialChannel.Tc1), "the press landed during the pause");

        bench.Gate = true;
        bench.Frame();
        Assert.Equal(DialState.Running, bench.Status.State);
        Assert.Equal(DialerMessages.Dialing(DialChannel.Tc1), bench.Status.Message);
        bench.RunToEnd();
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(6.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(4, bench.Game.TotalPresses, "the press sent before the pause was confirmed after it");
    }

    [Test]
    public static void Dialer_GateTimeoutCancels()
    {
        var bench = new Bench();
        bench.Settings.Timing.GatePauseTimeoutMs = 1000;
        bench.Game.Configure(DialChannel.Tc1, 2, 1, 0, 10).Configure(DialChannel.Abs, 5, 1, 0, 10);
        bench.Start("Timeout", (DialChannel.Tc1, 6), (DialChannel.Abs, 7));
        bench.RunUntilPresses(1);
        bench.Gate = false;
        bench.Frames(50);
        Assert.Equal(DialState.Paused, bench.Status.State, "within the timeout");
        bench.Frames(20);
        Assert.Equal(DialState.Cancelled, bench.Status.State, "after the timeout");
        Assert.Equal(DialerMessages.GateCancelled, bench.Status.Message);
        Assert.Equal(ChannelResult.Cancelled, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(ChannelResult.Cancelled, bench.Progress(DialChannel.Abs).Result, "pending channels too");
        Assert.Equal(ChannelResult.Skipped, bench.Progress(DialChannel.Tc2).Result, "skipped stays skipped");
        Assert.True(MathUtil.IsFinite(bench.Status.FinishedAt), "finish time");
        bench.Gate = true;
        bench.Frames(60);
        Assert.Equal(1, bench.Game.TotalPresses, "a cancelled job never presses again");

        // Timeout 0: cancelled as soon as the gate closes.
        bench.Settings.Timing.GatePauseTimeoutMs = 0;
        bench.Start("Zero", (DialChannel.Abs, 7));
        bench.Gate = false;
        bench.Frame();
        Assert.Equal(DialState.Cancelled, bench.Status.State);
        Assert.Equal(DialerMessages.GateCancelled, bench.Status.Message);
    }

    [Test]
    public static void Dialer_StartWhileRunningReplacesTheJob()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 2, 1, 0, 10);
        bench.Start("A", (DialChannel.Tc1, 8));
        bench.RunUntilPresses(3);
        Assert.Equal(4.0, bench.Game.GetValue(DialChannel.Tc1), "third press still in flight");
        int version = bench.Status.Version;

        bench.Start("B", (DialChannel.Tc1, 3));
        Assert.Equal(DialState.Running, bench.Status.State, "replaced, not reported as cancelled");
        Assert.Equal("B", bench.Status.Label);
        Assert.True(bench.Status.Version != version, "version bumped");
        Assert.Equal(0, bench.Progress(DialChannel.Tc1).Presses, "progress reset");
        Assert.Equal(ChannelResult.Pending, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(3.0, bench.Progress(DialChannel.Tc1).Target);

        bench.RunToEnd();
        Assert.Equal(DialState.Completed, bench.Status.State);
        Assert.Equal(3.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(2, bench.Progress(DialChannel.Tc1).Presses, "5 -> 3 after the old press landed");
        Assert.Equal(5, bench.Game.TotalPresses);
        Assert.Equal(TcDown, bench.Game.PressedRoles[3]);
        double landed = bench.Game.ChangeTimes[2];
        Assert.True(bench.Game.PressTimes[3] >= landed + (DialTiming.DefaultGapMs / 1000.0) - TimeEpsilon, "the new job waited for the old press");

        // A new job that does not dial the in-flight channel does not wait for it (only for the button release + gap).
        var other = new Bench();
        other.Game.Configure(DialChannel.Tc1, 2, 1, 0, 10).Configure(DialChannel.Abs, 5, 1, 0, 10);
        other.Game.SetLatency(DialChannel.Tc1, 30);
        other.Start("A", (DialChannel.Tc1, 8));
        other.RunUntilPresses(1);
        double firstPress = other.Game.PressTimes[0];
        other.Start("B", (DialChannel.Abs, 6));
        other.RunToEnd();
        Assert.Equal(6.0, other.Game.GetValue(DialChannel.Abs));
        double secondPress = other.Game.PressTimes[1];
        double releaseGap = (DialTiming.DefaultPressMs + DialTiming.DefaultGapMs) / 1000.0;
        Assert.True(secondPress >= firstPress + releaseGap - TimeEpsilon, "released for the gap");
        Assert.True(secondPress < firstPress + (30 * FakeDialGame.FrameSeconds), "did not wait for the slow TC press");
    }

    [Test]
    public static void Dialer_CancelStopsPressing()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 2, 1, 0, 10).Configure(DialChannel.Abs, 5, 1, 0, 10);
        bench.Start("Cancel me", (DialChannel.Tc1, 8), (DialChannel.Abs, 7));
        bench.RunUntilPresses(2);
        Assert.Equal(1, bench.Game.PendingEffects, "second press in flight");

        string reason = "Cancelled by the test";
        bench.Dialer.Cancel(reason);
        Assert.Equal(DialState.Cancelled, bench.Status.State);
        Assert.True(ReferenceEquals(reason, bench.Status.Message), "reason assigned by reference");
        Assert.Equal(ChannelResult.Cancelled, bench.Progress(DialChannel.Tc1).Result, "current channel");
        Assert.Equal(ChannelResult.Cancelled, bench.Progress(DialChannel.Abs).Result, "pending channel");
        Assert.Equal(ChannelResult.Skipped, bench.Progress(DialChannel.BrakeBias).Result);
        Assert.True(bench.Status.CurrentChannel == null, "no current channel");
        Assert.False(bench.Dialer.IsBusy, "not busy");
        Assert.Near(bench.Game.Now, bench.Status.FinishedAt, TimeEpsilon, "finished now");
        bench.Frames(120);
        Assert.Equal(2, bench.Game.TotalPresses, "no press after cancel");
        Assert.Equal(0, bench.Game.PendingEffects, "the press already sent still landed");
        Assert.Equal(4.0, bench.Game.GetValue(DialChannel.Tc1));

        int version = bench.Status.Version;
        bench.Dialer.Cancel(null);
        Assert.Equal(version, bench.Status.Version, "cancel when idle is a no-op");

        bench.Start("Again", (DialChannel.Tc1, 2));
        bench.Frames(3);
        bench.Dialer.Cancel(null);
        Assert.Equal(DialerMessages.Cancelled, bench.Status.Message, "default reason");
    }

    [Test]
    public static void Dialer_PressTimingRespectsReleaseAndGap()
    {
        var bench = new Bench();
        bench.Settings.Timing.GapMs = 300;
        bench.Game.Configure(DialChannel.Tc1, 0, 1, 0, 10).SetLatency(DialChannel.Tc1, 10);
        bench.Start("Slow game", (DialChannel.Tc1, 5));
        bench.RunToEnd();

        Assert.Equal(5, bench.Game.TotalPresses, "latency below the confirmation timeout causes no extra press");
        double releaseGap = (DialTiming.DefaultPressMs + 300) / 1000.0;
        for (int k = 1; k < bench.Game.TotalPresses; k++)
        {
            double previous = bench.Game.PressTimes[k - 1];
            double press = bench.Game.PressTimes[k];
            double change = bench.Game.ChangeTimes[k - 1];
            Assert.True(press - previous >= releaseGap - TimeEpsilon, "press " + k + " too soon after the previous press");
            Assert.True(press >= change + 0.3 - TimeEpsilon, "press " + k + " within the gap after the change");
        }

        // Default timing and a quick game.
        var quick = new Bench();
        quick.Game.Configure(DialChannel.Tc1, 0, 1, 0, 10).SetLatency(DialChannel.Tc1, 2);
        quick.Start("Quick", (DialChannel.Tc1, 6));
        quick.RunToEnd();
        Assert.Equal(6, quick.Game.TotalPresses);
        double minimum = (DialTiming.DefaultPressMs + DialTiming.DefaultGapMs) / 1000.0;
        for (int k = 1; k < quick.Game.TotalPresses; k++)
        {
            Assert.True(quick.Game.PressTimes[k] - quick.Game.PressTimes[k - 1] >= minimum - TimeEpsilon, "press " + k);
        }
    }

    [Test]
    public static void Dialer_MaxPressesIsASafetyStop()
    {
        var bench = new Bench();
        bench.Settings.Timing.MaxPressesPerChannel = 5;
        bench.Game.Configure(DialChannel.Tc1, 0, 1, 0, 10);
        bench.Start("Far", (DialChannel.Tc1, 10));
        bench.RunToEnd();

        Assert.Equal(ChannelResult.MaxPresses, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(5, bench.Game.TotalPresses);
        Assert.Equal(5.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal("Failed: TC too many presses", bench.Status.Message);
    }

    [Test]
    public static void Dialer_EmptyAndNullRequests()
    {
        var bench = new Bench();
        int version = bench.Status.Version;
        bench.Dialer.Start(null, bench.Game.Now);
        Assert.Equal(DialState.Idle, bench.Status.State, "null ignored");
        Assert.Equal(version, bench.Status.Version);

        bench.Start("Empty");
        Assert.Equal(DialState.Completed, bench.Status.State, "finished at once");
        Assert.Equal(DialerMessages.NothingToDial, bench.Status.Message);
        Assert.Equal(bench.Status.StartedAt, bench.Status.FinishedAt);
        Assert.False(bench.Dialer.IsBusy, "not busy");
        for (int i = 0; i < DialChannels.Count; i++)
        {
            Assert.Equal(ChannelResult.Skipped, bench.Status.Channels[i].Result);
        }

        bench.Frames(30);
        Assert.Equal(0, bench.Game.TotalPresses);
    }

    [Test]
    public static void Dialer_WithoutCarUsesScratchLearning()
    {
        var bench = new Bench { Car = null };
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10).SetInverted(DialChannel.Tc1);
        bench.Start("NoCar", (DialChannel.Tc1, 5));
        bench.RunToEnd();
        Assert.Equal(ChannelResult.Reached, bench.Progress(DialChannel.Tc1).Result);
        Assert.Equal(4, bench.Game.TotalPresses, "one wasted press");
        Assert.False(bench.Dialer.LearningChanged, "scratch learning is never reported");

        bench.Game.ClearLog();
        bench.Start("NoCar2", (DialChannel.Tc1, 3));
        bench.RunToEnd();
        Assert.Equal(3.0, bench.Game.GetValue(DialChannel.Tc1));
        Assert.Equal(4, bench.Game.TotalPresses, "scratch learning starts over with every job");
    }

    [Test]
    public static void Dialer_WrapAroundReturnsToTheLimit()
    {
        var bench = new Bench();
        bench.Game.Configure(DialChannel.Tc1, 8, 1, 0, 10).SetWraps(DialChannel.Tc1);
        bench.Start("Beyond", (DialChannel.Tc1, 12));
        bench.RunToEnd();

        ChannelProgress tc = bench.Progress(DialChannel.Tc1);
        Assert.Equal(ChannelResult.LimitReached, tc.Result);
        Assert.Equal(10.0, bench.Game.GetValue(DialChannel.Tc1), "back at the last value before the wrap");
        Assert.Equal(ChannelLearning.IncreaseRaises, bench.Learning(DialChannel.Tc1).Direction, "a wrap is not a reversed direction");
        Assert.True(tc.Presses <= 2 + 1 + 10 + 1 + 10, "bounded: " + tc.Presses + " presses");
    }

    [Test]
    public static void Dialer_FinalStateRules()
    {
        var bench = new Bench();
        bench.Settings.GetBinding(DialChannel.Tc3).Enabled = false;
        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10).Configure(DialChannel.Abs, 5, 1, 0, 10);

        bench.Start("Skipped only", (DialChannel.Tc3, 4));
        bench.RunToEnd();
        Assert.Equal(DialState.Completed, bench.Status.State, "skipped channels are neither success nor failure");

        bench.Game.SetBound(DialChannel.Tc1, false);
        bench.Start("Half", (DialChannel.Tc1, 4), (DialChannel.Abs, 6));
        bench.RunToEnd();
        Assert.Equal(DialState.Partial, bench.Status.State);
        Assert.Equal("Partly done: TC no response", bench.Status.Message, "names the first failure");

        bench.Game.SetBound(DialChannel.Abs, false);
        bench.Start("None", (DialChannel.Tc1, 4), (DialChannel.Abs, 8));
        bench.RunToEnd();
        Assert.Equal(DialState.Failed, bench.Status.State);
        Assert.Equal(ChannelResult.NoResponse, bench.Progress(DialChannel.Abs).Result);
    }

    [Test]
    public static void Dialer_NeverPressesWhenNotRunningAndVersionTracksChanges()
    {
        var bench = new Bench();
        bench.Frames(100);
        Assert.Equal(0, bench.Game.TotalPresses, "idle");

        bench.Game.Configure(DialChannel.Tc1, 3, 1, 0, 10).SetBound(DialChannel.Tc1, false);
        bench.Start("Wait", (DialChannel.Tc1, 4));
        bench.Frame();
        Assert.Equal(1, bench.Game.TotalPresses, "first press in the first frame");
        int version = bench.Status.Version;
        bench.Frames(20);
        Assert.Equal(version, bench.Status.Version, "nothing visible changes while waiting");
        bench.RunToEnd();
        int finished = bench.Game.TotalPresses;
        bench.Frames(100);
        Assert.Equal(finished, bench.Game.TotalPresses, "finished jobs never press");
    }

    [Test]
    public static void Dialer_SteadyStateUpdateDoesNotAllocate()
    {
        var bench = new Bench();
        bench.Settings.Timing.MaxPressesPerChannel = DialTiming.MaxMaxPressesPerChannel;
        bench.Settings.Timing.GatePauseTimeoutMs = DialTiming.MaxGatePauseTimeoutMs;
        bench.Game.Configure(DialChannel.BrakeBias, 40.0, 0.1, 0, 100).SetLatency(DialChannel.BrakeBias, 1);
        bench.Game.Configure(DialChannel.Tc1, 0, 1, 0, 10);

        // Warm-up (JIT, first-use paths): both directions, a pause and a cancel.
        bench.Start("Warm-up", (DialChannel.BrakeBias, 60.0), (DialChannel.Tc1, 3));
        bench.RunToEnd();
        bench.Start("Warm-up 2", (DialChannel.BrakeBias, 40.0), (DialChannel.Tc1, 0));
        bench.Frames(5);
        bench.Gate = false;
        bench.Frames(5);
        bench.Gate = true;
        bench.RunToEnd();
        bench.Dialer.Cancel(null);
        bench.Game.ClearLog();

        bench.Request.Clear("Measured");
        bench.Request.SetTarget(DialChannel.BrakeBias, 60.0);
        bench.Request.SetTarget(DialChannel.Tc1, 3);

        // The monitoring counter has allocation-context granularity: one byte per frame would exceed the allowance.
        const int IdleFrames = 10000;
        const long AllowanceBytes = 8 * 1024;
        AppDomain.MonitoringIsEnabled = true;
        long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;

        for (int i = 0; i < IdleFrames; i++)
        {
            bench.Frame();
        }

        bench.Dialer.Start(bench.Request, bench.Game.Now);
        int frames = 0;
        while (bench.Dialer.IsBusy && frames < MaxFrames)
        {
            bench.Frame();
            frames++;
        }

        bench.Request.SetTarget(DialChannel.BrakeBias, 50.0);
        bench.Request.SetTarget(DialChannel.Tc1, 1);
        bench.Dialer.Start(bench.Request, bench.Game.Now);
        bench.Frame();
        bench.Gate = false;
        for (int i = 0; i < IdleFrames; i++)
        {
            bench.Frame();
        }

        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        bench.Gate = true;
        Assert.True(frames < MaxFrames, "the measured job finished");
        Assert.Equal(DialState.Paused, bench.Status.State, "the last job was paused");
        Assert.True(bench.Game.TotalPresses >= 200, "the measured job pressed: " + bench.Game.TotalPresses);
        Assert.True(allocated <= AllowanceBytes, "allocated " + allocated + " bytes");
    }

    // ---------------------------------------------------------------------------------------------------

    private static int CountRole(FakeDialGame game, string role)
    {
        int count = 0;
        for (int i = 0; i < game.PressedRoles.Count; i++)
        {
            if (game.PressedRoles[i] == role)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The pressed roles, collapsed into runs, must be exactly <paramref name="runs"/> (e.g. TC+, ABS+, BB+).</summary>
    private static void AssertRoles(FakeDialGame game, params string[] runs)
    {
        int run = -1;
        string previous = null;
        for (int i = 0; i < game.PressedRoles.Count; i++)
        {
            string role = game.PressedRoles[i];
            if (role == previous)
            {
                continue;
            }

            run++;
            Assert.True(run < runs.Length, "unexpected role " + role + " at press " + i);
            Assert.Equal(runs[run], role, "role run " + run);
            previous = role;
        }

        Assert.Equal(runs.Length, run + 1, "role runs");
    }

    private static SpeedDialSettings CreateSettings()
    {
        var settings = new SpeedDialSettings();
        settings.Normalize();
        return settings;
    }

    /// <summary>One dialer driving one fake game, with default settings and a fresh car.</summary>
    private sealed class Bench
    {
        public readonly FakeDialGame Game = new FakeDialGame();
        public readonly SpeedDialSettings Settings = CreateSettings();
        public readonly DialRequest Request = new DialRequest();
        public readonly Dialer Dialer;

        public Bench()
        {
            Dialer = new Dialer(Game, NullLog.Instance);
            Telemetry = Game;
        }

        public SpeedDialCarData Car { get; set; } = SpeedDialCarData.Create("FakeGame", "test_car", "Test car");

        public IDialTelemetry Telemetry { get; set; }

        public bool Gate { get; set; } = true;

        public DialStatus Status => Dialer.Status;

        public ChannelProgress Progress(DialChannel channel) => Dialer.Status.Get(channel);

        public ChannelLearning Learning(DialChannel channel) => Car.GetLearning(channel);

        public void Start(string label, params (DialChannel Channel, double Target)[] targets)
        {
            Request.Clear(label);
            foreach ((DialChannel Channel, double Target) target in targets)
            {
                Request.SetTarget(target.Channel, target.Target);
            }

            Dialer.Start(Request, Game.Now);
        }

        public void Frame()
        {
            Game.Step();
            Dialer.Update(Game.Now, Gate, Telemetry, Settings, Car);
        }

        public void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Frame();
            }
        }

        public void RunToEnd()
        {
            int frames = 0;
            while (Dialer.IsBusy)
            {
                if (++frames > MaxFrames)
                {
                    Assert.Fail("the job did not finish within " + MaxFrames + " frames");
                }

                Frame();
            }
        }

        public void RunUntilPresses(int presses)
        {
            int frames = 0;
            while (Game.TotalPresses < presses)
            {
                if (++frames > MaxFrames)
                {
                    Assert.Fail("press " + presses + " never came");
                }

                Frame();
            }
        }

        public void RunUntilChanges(int changes)
        {
            int frames = 0;
            while (Game.ChangeTimes.Count < changes)
            {
                if (++frames > MaxFrames)
                {
                    Assert.Fail("change " + changes + " never came");
                }

                Frame();
            }
        }
    }
}
