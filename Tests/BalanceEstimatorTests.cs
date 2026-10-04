using System;
using System.Collections.Generic;
using System.Globalization;
using DivebombLogistics.Core;
using DivebombLogistics.Haptics.Balance;
using DivebombLogistics.Haptics.Settings;
using DivebombLogistics.Tests.Sim;

namespace DivebombLogistics.Tests;

/// <summary>
/// Balance filters and the understeer/oversteer estimator: spec section 8 scenarios and acceptance criteria,
/// driven through the public estimator API by the deterministic <see cref="VehicleSimulator"/>.
/// </summary>
internal sealed class BalanceEstimatorTests
{
    private const double Dt = 1.0 / 60.0;

    /// <summary>Every scenario starts with this much straight driving so the load blank (2 s) has passed.</summary>
    private const double WarmUpSeconds = 3.0;

    // =====================================================================================================
    // Filters
    // =====================================================================================================

    [Test]
    public void Filter_EmaStartsAtFirstSampleAndConverges()
    {
        var ema = new Ema();
        Assert.False(ema.HasValue);
        Assert.True(double.IsNaN(ema.Value), "value before the first sample");

        Assert.Near(5.0, ema.Update(5.0, 0.1), 1e-12, "first sample initializes");
        for (int i = 0; i < 200; i++)
        {
            ema.Update(1.0, 0.1);
        }

        Assert.Near(1.0, ema.Value, 1e-6, "converged");

        var half = new Ema();
        half.Update(0.0, 0.5);
        Assert.Near(0.5, half.Update(1.0, 0.5), 1e-12, "alpha 0.5 halves the gap");
    }

    [Test]
    public void Filter_EmaIgnoresNonFiniteSamplesAndResets()
    {
        var ema = new Ema();
        ema.Update(2.0, 0.5);
        Assert.Near(2.0, ema.Update(double.NaN, 0.5), 1e-12, "NaN ignored");
        Assert.Near(2.0, ema.Update(double.PositiveInfinity, 0.5), 1e-12, "infinity ignored");
        ema.Reset();
        Assert.False(ema.HasValue);
        Assert.Near(7.0, ema.Update(7.0, 0.5), 1e-12, "restarts at the next sample");
    }

    [Test]
    public void Filter_Median3RejectsSingleSpikesAndDelaysRampsByOneSample()
    {
        var median = new Median3();
        Assert.True(double.IsNaN(median.Update(double.NaN)), "empty filter returns NaN");
        Assert.Near(1.0, median.Update(1.0), 1e-12, "first sample fills the window");
        Assert.Near(1.0, median.Update(50.0), 1e-12, "spike right after start rejected");
        Assert.Near(1.0, median.Update(1.0), 1e-12);
        Assert.Near(1.0, median.Update(-50.0), 1e-12, "negative spike rejected");
        Assert.Near(1.0, median.Update(1.0), 1e-12);

        var ramp = new Median3();
        ramp.Update(0.0);
        ramp.Update(1.0);
        Assert.Near(1.0, ramp.Update(2.0), 1e-12, "ramp is delayed by exactly one sample");
        Assert.Near(2.0, ramp.Update(3.0), 1e-12);
        Assert.Near(2.0, ramp.Update(double.NaN), 1e-12, "NaN ignored");
    }

    [Test]
    public void Filter_AttackReleaseFollowsTimeConstantsAndSnapsToTarget()
    {
        var envelope = new AttackRelease();
        envelope.Update(1.0, 0.03, 0.03, 0.15);
        Assert.Near(1.0 - Math.Exp(-1.0), envelope.Value, 1e-9, "one attack time constant");

        var slow = new AttackRelease();
        for (int i = 0; i < 600; i++)
        {
            slow.Update(1.0, Dt, 0.03, 0.15);
        }

        Assert.Equal(1.0, slow.Value, "snaps onto the target");
        slow.Update(0.0, 0.15, 0.03, 0.15);
        Assert.Near(Math.Exp(-1.0), slow.Value, 1e-9, "one release time constant");
        for (int i = 0; i < 120; i++)
        {
            slow.Update(0.0, Dt, 0.03, 0.15);
        }

        Assert.Equal(0.0, slow.Value, "decays to exactly 0");
        Assert.Equal(0.0, slow.Update(0.5, 0.0, 0.03, 0.15), "dt 0 changes nothing");
        Assert.Equal(1.0, new AttackRelease().Update(1.0, Dt, 0.0, 0.0), "zero attack is instant");
        Assert.Equal(0.0, new AttackRelease().Update(double.NaN, Dt, 0.03, 0.15), "NaN target is 0");
    }

    [Test]
    public void Filter_HysteresisArmsAtThresholdAndDisarmsAtZero()
    {
        var gate = new HysteresisGate();
        Assert.Equal(0.0, gate.Apply(0.02, 0.03), "below the arm level");
        Assert.False(gate.Armed);
        Assert.Equal(0.03, gate.Apply(0.03, 0.03), "arms at the threshold");
        Assert.True(gate.Armed);
        Assert.Equal(0.01, gate.Apply(0.01, 0.03), "stays armed while raw > 0");
        Assert.Equal(0.0, gate.Apply(0.0, 0.03), "disarms at 0");
        Assert.False(gate.Armed);
        Assert.Equal(0.0, gate.Apply(0.02, 0.03), "must reach the arm level again");
        Assert.Equal(0.0, gate.Apply(double.NaN, 0.03), "NaN disarms");
        Assert.Equal(0.001, new HysteresisGate().Apply(0.001, 0.0), "arm level 0 passes everything positive");
    }

    [Test]
    public void Filter_SignVoteDecidesOnlyOnClearEvidence()
    {
        var vote = new SignVote(10, 0.5);
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(SignVerdict.Pending, vote.Add(-1));
        }

        Assert.Equal(SignVerdict.Inverted, vote.Add(-1), "10 of 10 disagree");
        Assert.Equal(0, vote.Count, "restarts after a verdict");

        for (int i = 0; i < 9; i++)
        {
            vote.Add(i < 8 ? 1 : -1);
        }

        Assert.Equal(SignVerdict.Confirmed, vote.Add(1), "9 of 10 agree");

        for (int i = 0; i < 9; i++)
        {
            vote.Add(i % 2 == 0 ? 1 : -1);
        }

        Assert.Equal(SignVerdict.Pending, vote.Add(1), "ambiguous evidence restarts");
        Assert.Equal(SignVerdict.Pending, vote.Add(0), "zero votes are ignored");
        Assert.Equal(0, vote.Count);
    }

    // =====================================================================================================
    // Acceptance: no output where there is no balance event
    // =====================================================================================================

    [Test]
    public void Straight_ProducesNoOutputs()
    {
        // Typical sensor noise and a not yet learned steering offset.
        var car = new SimCar { Theta0Deg = 0.8 };
        SimScript script = WarmUp(40.0);
        script.Hold(20.0, "straight");

        Harness h = Run(car, script, SimNoise.Typical());
        PhaseStats straight = h.Phase("straight");
        Assert.Equal(0.0, straight.MaxUs, "understeer on a straight");
        Assert.Equal(0.0, straight.MaxOs, "oversteer on a straight");
        Assert.Equal(straight.Ticks, straight.ActiveTicks, "active on the straight");
        Assert.Equal(BalancePath.Model, h.Outputs.Path);
    }

    [Test]
    public void PitLane_ProducesNoOutputsAndNoLearning()
    {
        var car = new SimCar();
        SimScript script = WarmUp(20.0);
        script.Steer(1.0, car.GripLimitSteerDeg(20.0) * 1.5, "pit").PitLane = true;
        script.Hold(2.0, "pit").PitLane = true;
        script.SlideTo(0.5, 0.6, "pit").PitLane = true;
        script.Hold(1.0, "pit").PitLane = true;

        long lateralSamplesBefore = -1;
        Harness h = Run(car, script, SimNoise.Typical(), onTick: (sim, o) =>
        {
            if (sim.Segment.Label == "pit")
            {
                if (lateralSamplesBefore < 0)
                {
                    lateralSamplesBefore = o.SamplesAy;
                }

                Assert.False(o.LearningActive, "learning in the pit lane");
                Assert.Equal(BalanceGate.PitLane, o.Gate);
            }
        });

        PhaseStats pit = h.Phase("pit");
        Assert.Equal(0.0, pit.MaxUs, "understeer in the pit lane");
        Assert.Equal(0.0, pit.MaxOs, "oversteer in the pit lane");
        Assert.Equal(lateralSamplesBefore, h.Outputs.SamplesAy, "lateral samples added in the pit lane");

        // Control: the same driving off the pit lane is clearly detected.
        SimScript control = WarmUp(20.0);
        control.Steer(1.0, car.GripLimitSteerDeg(20.0) * 1.5, "corner");
        control.Hold(2.0, "corner");
        Assert.True(Run(car, control, SimNoise.Typical()).Phase("corner").MaxUs > 0.3, "control run shows understeer");
    }

    [Test]
    public void NormalTurnIn_ProducesNoUndersteerSpike()
    {
        var car = new SimCar();
        SimScript script = WarmUp(30.0);
        script.Steer(0.3, car.SteerDegFor(8.0, 30.0), "turn-in");
        script.Hold(2.0, "corner");
        script.Steer(0.4, 0.0, "unwind");
        script.Hold(2.0, "exit");

        Harness h = Run(car, script, SimNoise.Typical());
        foreach (string phase in new[] { "turn-in", "corner", "unwind", "exit" })
        {
            PhaseStats stats = h.Phase(phase);
            Assert.True(stats.MaxUs < 0.05, phase + ": understeer spike " + F(stats.MaxUs));
            Assert.True(stats.MaxOs < 0.1, phase + ": oversteer spike " + F(stats.MaxOs));
        }
    }

    [Test]
    public void QuickDirectionChange_IsNotCountersteer()
    {
        // A flick from 0.3 rad/s left to right in 0.3 s: the wheel crosses centre while the car still yaws left
        // well above CsROnset (the plain sign test of the spec would report countersteer here). Yaw-ratio
        // transients from the default yaw lag stay below the tag threshold (a learned lag removes them).
        var car = new SimCar();
        double tagThreshold = new BalanceTuning().TagThreshold;
        double steer = car.SteerDegFor(9.0, 30.0);
        SimScript script = WarmUp(30.0);
        script.Steer(0.35, steer, "left");
        script.Hold(1.0, "left");
        script.Steer(0.3, -steer, "switch");
        script.Hold(1.0, "right");
        script.Steer(0.4, 0.0, "exit");
        script.Hold(1.0, "exit");

        Harness h = Run(car, script, SimNoise.Typical());
        foreach (string phase in new[] { "left", "switch", "right", "exit" })
        {
            PhaseStats stats = h.Phase(phase);
            Assert.Equal(0, stats.CountersteerTicks, phase + ": countersteer detected");
            Assert.True(stats.MaxOs < tagThreshold, phase + ": oversteer " + F(stats.MaxOs));
            Assert.True(stats.MaxUs < tagThreshold, phase + ": understeer " + F(stats.MaxUs));
        }
    }

    [Test]
    public void KerbSpikes_AreRejectedByMedianFilter()
    {
        var car = new SimCar();
        SimScript script = WarmUp(30.0);
        script.Steer(0.4, car.SteerDegFor(9.0, 30.0), "turn-in");
        script.Hold(1.0, "corner");
        script.Hold(3.0, "kerbs").With(s =>
        {
            s.KerbSpikeInterval = 0.25;
            s.KerbSpikeAmplitude = 1.5;
            s.Surface = SurfaceKind.Kerb;
        });

        int spikes = 0;
        Harness h = Run(car, script, SimNoise.Typical(), onTick: (sim, o) =>
        {
            if (sim.Segment.Label == "kerbs" && Math.Abs(sim.State.R - sim.TrueYawRate) > 1.0)
            {
                spikes++;
            }
        });

        Assert.True(spikes >= 10, "scenario contains kerb spikes: " + spikes);
        PhaseStats kerbs = h.Phase("kerbs");
        Assert.Equal(0.0, kerbs.MaxUs, "understeer from kerb spikes");
        Assert.Equal(0.0, kerbs.MaxOs, "oversteer from kerb spikes");
    }

    // =====================================================================================================
    // Acceptance: understeer and oversteer scenarios
    // =====================================================================================================

    [Test]
    public void FrontSaturation_ReportsUndersteerOnlyWithEntryAndExitTags()
    {
        var car = new SimCar();
        SimScript script = WarmUp(30.0);
        script.Steer(0.4, 60.0, "turn-in");
        script.Hold(1.0, "linear");
        script.Steer(1.0, 110.0, "saturating");
        script.Hold(1.5, "entry").Pedals(0.0, 0.3);
        script.Hold(1.5, "exit").Pedals(0.6, 0.0);

        Harness h = Run(car, script, SimNoise.Typical());
        Assert.True(h.Phase("linear").MaxUs < 0.05, "no understeer below the grip limit: " + F(h.Phase("linear").MaxUs));

        PhaseStats entry = h.Phase("entry");
        PhaseStats exit = h.Phase("exit");
        Assert.True(entry.LastUs > 0.5, "understeer at the grip limit: " + F(entry.LastUs));
        Assert.True(exit.MinUs > 0.5, "understeer holds: " + F(exit.MinUs));
        Assert.Equal(0.0, Math.Max(entry.MaxOs, exit.MaxOs), "no oversteer while understeering");
        Assert.True(entry.EntryTicks > 0 && entry.ExitTicks == 0, "EntryUndersteer tag while braking");
        Assert.True(exit.ExitTicks > 0 && exit.EntryTicks == 0, "ExitUndersteer tag on throttle");
        Assert.Equal(BalancePath.Model, h.Outputs.Path);
    }

    [Test]
    public void Understeer_IsIndependentOfTheSampleRate()
    {
        // dt comes from the timestamps and every filter is defined by time constants, so 30, 60 and 120 Hz
        // telemetry must give the same steady output.
        double[] rates = { 30.0, 60.0, 120.0 };
        var levels = new double[rates.Length];
        for (int i = 0; i < rates.Length; i++)
        {
            SimScript script = UndersteerScript(30.0, out SimCar car);
            levels[i] = Run(car, script, SimNoise.Typical(), options: new SimOptions { RateHz = rates[i] }).Phase("understeer").LastUs;
        }

        Assert.True(levels[1] > 0.5, "understeer at 60 Hz: " + F(levels[1]));
        Assert.Near(levels[1], levels[0], 0.03, "30 Hz vs 60 Hz");
        Assert.Near(levels[1], levels[2], 0.03, "120 Hz vs 60 Hz");
    }

    [Test]
    public void PowerOversteer_ReportsOversteerWithPowerTag()
    {
        Harness h = RunRearSlide(throttle: 1.0);
        PhaseStats sliding = h.Phase("sliding");
        Assert.True(sliding.LastOs > 0.5, "oversteer: " + F(sliding.LastOs));
        Assert.Equal(0.0, h.Phase("slide").MaxUs + sliding.MaxUs, "no understeer during the slide");
        Assert.True(sliding.PowerTicks > 0, "PowerOversteer tag");
        Assert.Equal(0, sliding.LiftTicks, "no LiftOrBrakeOversteer tag on full throttle");
    }

    [Test]
    public void LiftOffOversteer_ReportsLiftOrBrakeTag()
    {
        Harness h = RunRearSlide(throttle: 0.0);
        PhaseStats sliding = h.Phase("sliding");
        Assert.True(sliding.LastOs > 0.5, "oversteer: " + F(sliding.LastOs));
        Assert.True(sliding.LiftTicks > 0, "LiftOrBrakeOversteer tag");
        Assert.Equal(0, sliding.PowerTicks, "no PowerOversteer tag off throttle");
    }

    [Test]
    public void Countersteer_NeverProducesUndersteerAndHoldsBaseOversteer()
    {
        var car = new SimCar();
        SimScript script = WarmUp(30.0);
        script.Steer(0.4, car.SteerDegFor(10.0, 30.0), "turn-in");
        script.Hold(1.0, "corner");
        script.SlideTo(0.3, 0.8, "slide").Pedals(1.0, 0.0);
        script.To(0.3, -25.0, 30.0, "counter", slideYaw: 0.3).With(s => s.HoldPath = true);
        script.Hold(1.2, "held").With(s => s.HoldPath = true);

        double csBase = new BalanceTuning().CsBase;
        double minHeldOs = double.MaxValue;
        Harness h = Run(car, script, SimNoise.Typical(), onTick: (sim, o) =>
        {
            if (sim.Segment.Label == "held" && sim.SegmentTime > 0.2)
            {
                minHeldOs = Math.Min(minHeldOs, o.Oversteer);
                Assert.True(o.Countersteer, "Countersteer tag while holding opposite lock");
                Assert.True(o.OsCountersteer >= csBase, "countersteer detector at or above its base: " + F(o.OsCountersteer));
            }
        });

        Assert.Equal(0.0, h.Phase("counter").MaxUs, "understeer while countersteering");
        Assert.Equal(0.0, h.Phase("held").MaxUs, "understeer while holding opposite lock");
        Assert.True(minHeldOs >= csBase, "oversteer at least CsBase: " + F(minHeldOs));
    }

    [Test]
    public void Spin_ForcesFullOversteerThenTimesOut()
    {
        var car = new SimCar();
        SimScript script = WarmUp(25.0);
        script.Steer(0.4, car.SteerDegFor(6.0, 25.0), "turn-in");
        script.Hold(1.0, "corner");
        script.SlideTo(0.5, 2.4, "spin-start").With(s => s.HoldPath = true);
        script.Hold(3.5, "spinning").With(s => s.HoldPath = true);

        double spinStart = double.NaN;
        double minOsDuringSpin = double.MaxValue;
        bool timedOut = false;
        double lastOs = double.NaN;
        var tuning = new BalanceTuning();
        Harness h = Run(car, script, SimNoise.Typical(), tuning: tuning, onTick: (sim, o) =>
        {
            if (double.IsNaN(spinStart) && o.Spin)
            {
                spinStart = sim.Time;
            }

            if (double.IsNaN(spinStart))
            {
                return;
            }

            double since = sim.Time - spinStart;
            if (since > 0.2 && since < tuning.SpinTimeout - 0.1)
            {
                minOsDuringSpin = Math.Min(minOsDuringSpin, o.Oversteer);
                Assert.Equal(0.0, o.Understeer, "understeer during a spin");
            }

            if (since > tuning.SpinTimeout + 0.1 && since < tuning.SpinTimeout + 0.5)
            {
                timedOut |= o.Gate == BalanceGate.SpinTimeout;
                Assert.False(o.Spin, "Spin tag after the timeout");
            }

            lastOs = o.Oversteer;
        });

        Assert.False(double.IsNaN(spinStart), "spin detected");
        Assert.True(Math.Abs(h.Phase("spinning").MaxBodySlipDeg) > tuning.BetaSpinDeg, "scenario exceeds the spin angle");
        Assert.True(minOsDuringSpin >= 0.99, "full oversteer during the spin: " + F(minOsDuringSpin));
        Assert.True(timedOut, "SpinTimeout gate after the timeout");
        Assert.True(lastOs < 0.01, "outputs faded after the timeout: " + F(lastOs));
    }

    [Test]
    public void MutualExclusion_NoUndersteerWhileOversteering()
    {
        var car = new SimCar();
        var tuning = new BalanceTuning();
        SimScript script = WarmUp(30.0);
        script.Steer(0.4, 60.0, "turn-in");
        script.Steer(1.0, 110.0, "saturating");
        script.Hold(1.0, "understeer");
        script.SlideTo(0.5, 1.0, "slide").Pedals(0.0, 0.0);
        script.Hold(0.5, "sliding").Pedals(0.0, 0.0);

        Harness h = Run(car, script, SimNoise.Typical(), tuning: tuning, onTick: (sim, o) =>
        {
            Assert.False(o.Oversteer > tuning.MutualExclusionOs && o.Understeer > 0.0,
                "understeer " + F(o.Understeer) + " while oversteer " + F(o.Oversteer) + " in " + sim.Segment.Label);
        });

        Assert.True(h.Phase("understeer").LastUs > 0.5, "understeer before the slide");
        Assert.True(h.Phase("sliding").LastOs > 0.5, "oversteer during the slide");
        Assert.Equal(0.0, h.Phase("sliding").LastUs, "understeer during the slide");
    }

    [Test]
    public void Hysteresis_PreventsChatterAroundOnset()
    {
        var car = new SimCar();
        var tuning = new BalanceTuning();
        double speed = 30.0;
        double Raw(double u) => MathUtil.Map(u, tuning.UsOnset, tuning.UsFull);
        double uA = tuning.UsOnset + 0.005;
        double uB = tuning.UsOnset + 0.02;
        double uC = tuning.UsOnset + 0.002;
        double uD = tuning.UsOnset - 0.01;
        Assert.True(Raw(uA) < tuning.Hysteresis && Raw(uB) > tuning.Hysteresis && Raw(uC) > 0.0, "scenario brackets the arm level");

        SimScript script = WarmUp(speed);
        script.Steer(1.5, SteerDegForUndersteer(car, uA, speed), "A");
        script.Hold(2.0, "A");
        script.Steer(0.3, SteerDegForUndersteer(car, uB, speed), "B");
        script.Hold(1.5, "B");
        script.Steer(0.3, SteerDegForUndersteer(car, uC, speed), "C");
        script.Hold(1.5, "C");
        script.Steer(0.3, SteerDegForUndersteer(car, uD, speed), "D");
        script.Hold(1.5, "D");
        script.Steer(0.3, SteerDegForUndersteer(car, uA, speed), "E");
        script.Hold(1.5, "E");

        Harness h = Run(car, script, tuning: tuning, profile: p => ExactModel(p, car));
        Assert.Equal(0.0, h.Phase("A").MaxUs, "not armed just above onset");
        Assert.True(h.Phase("B").LastUs > 0.04, "armed once above the hysteresis: " + F(h.Phase("B").LastUs));
        Assert.True(h.Phase("C").MinUs > 0.0, "stays armed while above onset: " + F(h.Phase("C").MinUs));
        Assert.Equal(0.0, h.Phase("D").LastUs, "disarmed below onset");
        Assert.Equal(0.0, h.Phase("E").MaxUs, "must reach the hysteresis again to re-arm");

        // Control: without hysteresis the same small excursion above onset produces output.
        var noHysteresis = new BalanceTuning { Hysteresis = 0.0 };
        SimScript control = WarmUp(speed);
        control.Steer(1.5, SteerDegForUndersteer(car, uA, speed), "A");
        control.Hold(2.0, "A");
        Harness c = Run(car, control, tuning: noHysteresis, profile: p => ExactModel(p, car));
        Assert.True(c.Phase("A").LastUs > 0.005, "control without hysteresis: " + F(c.Phase("A").LastUs));
    }

    [Test]
    public void Sensitivity_ScalesUndersteerAndOversteer()
    {
        var car = new SimCar();
        double[] percents = { 50.0, 100.0, 200.0 };
        var us = new double[percents.Length];
        var os = new double[percents.Length];
        for (int i = 0; i < percents.Length; i++)
        {
            double percent = percents[i];
            SimScript script = WarmUp(30.0);
            script.Steer(1.5, SteerDegForUndersteer(car, 0.25, 30.0), "saturating");
            script.Hold(2.0, "understeer");
            us[i] = Run(car, script, profile: p =>
            {
                ExactModel(p, car);
                p.UndersteerSensitivity = percent;
            }).Phase("understeer").LastUs;

            // Steady 4° drift on a straight: only the body-slip detector responds.
            SimScript drift = WarmUp(30.0);
            drift.SlideTo(0.5, 4.0 * MathUtil.DegToRad / car.SlideRecoveryTau, "drift-in");
            drift.Hold(4.0, "drift");
            os[i] = Run(car, drift, profile: p => p.OversteerSensitivity = percent).Phase("drift").LastOs;
        }

        Assert.Near(MathUtil.Map(0.25, 0.10, 0.45), us[1], 0.02, "understeer at 100 %");
        Assert.Near(MathUtil.Map(4.0, 1.5, 8.0), os[1], 0.02, "oversteer at 100 %");
        Assert.True(us[0] > 0.0 && us[0] < us[1] - 0.2 && us[2] > us[1] + 0.2, "understeer 50/100/200 %: " + F(us[0]) + " / " + F(us[1]) + " / " + F(us[2]));
        Assert.True(os[0] > 0.0 && os[0] < os[1] - 0.2 && os[2] > os[1] + 0.2, "oversteer 50/100/200 %: " + F(os[0]) + " / " + F(os[1]) + " / " + F(os[2]));
    }

    // =====================================================================================================
    // Acceptance: gates and edge cases (spec section 6)
    // =====================================================================================================

    [Test]
    public void Collision_BlanksOutputsForContactBlankTime()
    {
        AssertContactBlank(s => s.CollisionG = 5.0);
    }

    [Test]
    public void ContactCounter_BlanksOutputs()
    {
        AssertContactBlank(s => s.ContactEvent = true);
    }

    [Test]
    public void Airborne_FadesOutputs_GravityIncluded()
    {
        AssertAirborneFade(gravityIncluded: true);
    }

    [Test]
    public void Airborne_FadesOutputs_GravityExcluded()
    {
        AssertAirborneFade(gravityIncluded: false);
    }

    [Test]
    public void FrozenTelemetry_DecaysOutputsThenResumes()
    {
        var tuning = new BalanceTuning();
        SimScript script = UndersteerScript(30.0, out _);
        script.Hold(1.5, "frozen").Frozen = true;
        script.Hold(1.5, "resumed");

        bool frozenGate = false;
        double decayed = double.NaN;
        double decayCheck = tuning.FrozenTimeout + (5.0 * tuning.Release);
        Harness h = Run(new SimCar(), script, SimNoise.Typical(), tuning: tuning, onTick: (sim, o) =>
        {
            if (sim.Segment.Label != "frozen")
            {
                return;
            }

            if (sim.SegmentTime < tuning.FrozenTimeout - 0.05)
            {
                Assert.True(o.Understeer > 0.5, "held briefly while frames repeat: " + F(o.Understeer));
            }

            frozenGate |= o.Gate == BalanceGate.Frozen;
            if (double.IsNaN(decayed) && sim.SegmentTime >= decayCheck)
            {
                decayed = o.Understeer;
            }
        });

        Assert.True(frozenGate, "Frozen gate");
        Assert.True(decayed < 0.01, "decayed within FrozenTimeout + 5 release time constants: " + F(decayed));
        Assert.True(h.Phase("resumed").LastUs > 0.5, "resumes after the freeze: " + F(h.Phase("resumed").LastUs));
    }

    [Test]
    public void Reverse_ProducesNoOutputs()
    {
        SimScript script = WarmUp(10.0);
        script.Drive(2.0, 0.0, "stop");
        script.To(2.0, 90.0, -10.0, "reverse").Gear = -1;
        script.Hold(2.0, "reverse").Gear = -1;
        script.SlideTo(0.5, 0.5, "reverse").Gear = -1;

        Harness h = Run(new SimCar(), script, SimNoise.Typical());
        PhaseStats reverse = h.Phase("reverse");
        Assert.Equal(0.0, reverse.MaxUs, "understeer in reverse");
        Assert.Equal(0.0, reverse.MaxOs, "oversteer in reverse");
        Assert.Equal(BalanceGate.Reverse, h.Outputs.Gate);
    }

    [Test]
    public void LowSpeed_RampsOutputsWithSpeed()
    {
        var car = new SimCar();
        var tuning = new BalanceTuning();
        double slideYaw = 20.0 * MathUtil.DegToRad / car.SlideRecoveryTau;
        SimScript script = WarmUp(20.0);
        script.Steer(0.5, car.SteerDegFor(2.0, 20.0), "turn-in");
        script.SlideTo(1.0, slideYaw, "drift-in");
        script.Hold(3.0, "drift20");
        script.Drive(2.0, 11.5, "slowing");
        script.Hold(2.0, "drift11");
        script.Drive(2.0, 6.0, "slowing");
        script.Hold(1.5, "drift6");

        Harness h = Run(car, script, tuning: tuning);
        double ramp = (11.5 - tuning.VMin) / (tuning.VFull - tuning.VMin);
        Assert.True(h.Phase("drift20").LastOs > 0.95, "full speed: " + F(h.Phase("drift20").LastOs));
        Assert.Near(ramp, h.Phase("drift11").LastOs, 0.05, "scaled by the speed ramp");
        Assert.Equal(0.0, h.Phase("drift6").LastOs, "below VMin");
        Assert.Equal(BalanceGate.LowSpeed, h.Outputs.Gate);
    }

    [Test]
    public void Teleport_ClockJumpBlanksOutputs()
    {
        var tuning = new BalanceTuning();
        SimScript script = UndersteerScript(30.0, out _);
        script.Hold(4.0, "after-jump").TimeJump = 5.0;
        AssertBlankedThenActive(script, "after-jump", tuning);
    }

    [Test]
    public void Teleport_SpeedJumpBlanksOutputs()
    {
        var tuning = new BalanceTuning();
        SimScript script = WarmUp(45.0);
        script.Hold(1.0, "before");
        script.TeleportTo(4.0, 20.0, "after-jump");
        AssertBlankedThenActive(script, "after-jump", tuning);
    }

    [Test]
    public void SessionRestart_ClockRunningBackwardsRestartsTimeline()
    {
        var tuning = new BalanceTuning();
        SimScript script = WarmUp(30.0);
        script.Hold(4.0, "after-restart").TimeJump = -90.0;
        script.Steer(1.0, 110.0, "saturating");
        script.Hold(1.0, "understeer");

        Harness h = AssertBlankedThenActive(script, "after-restart", tuning);
        Assert.True(h.Phase("understeer").LastUs > 0.5, "detects understeer after the restart");
    }

    [Test]
    public void Rejoin_AfterGarageBlanksOutputs()
    {
        var tuning = new BalanceTuning();
        SimScript script = WarmUp(30.0);
        script.Hold(2.0, "garage").NotOnTrack = true;
        script.Hold(4.0, "after-jump");

        bool notOnTrack = false;
        AssertBlankedThenActive(script, "after-jump", tuning, (sim, o) => notOnTrack |= sim.Segment.Label == "garage" && o.Gate == BalanceGate.NotOnTrack);
        Assert.True(notOnTrack, "NotOnTrack gate in the garage");
    }

    [Test]
    public void ReplayAndPause_ProduceNoOutputs()
    {
        SimScript script = UndersteerScript(30.0, out _);
        script.Hold(1.5, "replay").Replay = true;
        script.Hold(1.0, "live");
        script.Hold(1.5, "paused").Paused = true;
        script.Hold(1.0, "live2");
        script.Hold(1.5, "spectating").Spectating = true;

        Harness h = Run(new SimCar(), script, SimNoise.Typical());
        Assert.True(h.Phase("replay").LastUs < 0.001 && h.Phase("replay").LastGate == BalanceGate.Replay, "replay gate");
        Assert.True(h.Phase("live").LastUs > 0.5, "live again");
        Assert.True(h.Phase("paused").LastUs < 0.001 && h.Phase("paused").LastGate == BalanceGate.Paused, "paused gate");
        Assert.True(h.Phase("live2").LastUs > 0.5, "live after pause");
        Assert.True(h.Phase("spectating").LastUs < 0.001 && h.Phase("spectating").LastGate == BalanceGate.Replay, "spectating uses the replay gate");
    }

    [Test]
    public void BackFromReplay_StartsFromFreshFilters()
    {
        // The replay starts mid-corner; back live the car is on a straight. Filter state from before the replay
        // (a large requested yaw rate) must not meet the new yaw rate and read as understeer.
        SimScript script = UndersteerScript(30.0, out _);
        script.Steer(0.5, 0.0, "replay").Replay = true;
        script.Hold(1.0, "replay").Replay = true;
        script.Hold(2.0, "live");

        Harness h = Run(new SimCar(), script, SimNoise.Typical());
        Assert.Equal(BalanceGate.Active, h.Phase("live").LastGate);
        Assert.Equal(0.0, h.Phase("live").MaxUs, "understeer from stale filters");
        Assert.Equal(0.0, h.Phase("live").MaxOs, "oversteer from stale filters");
    }

    [Test]
    public void MissingYawRate_UsesLateralFallbackWithReducedConfidence()
    {
        SimScript script = UndersteerScript(30.0, out _);
        Harness h = Run(new SimCar(), script, SimNoise.Typical(), options: new SimOptions { ReportYawRate = false });
        Assert.True(h.Phase("understeer").LastUs > 0.5, "understeer from ay / v: " + F(h.Phase("understeer").LastUs));
        Assert.Equal(BalancePath.Model, h.Outputs.Path);
        Assert.Near(0.1, h.Outputs.Confidence, 1e-9, "default confidence halved for the fallback");
    }

    // =====================================================================================================
    // Runtime calibration
    // =====================================================================================================

    [Test]
    public void SteeringSign_IsCorrectedWhenAdapterIsInverted()
    {
        var calibration = new SimCalibration { ForwardSignVerified = true, GravityIncluded = true };
        Harness h = RunGentleCorners(calibration, invertSteering: true, out double firstTurnIn, out double calibratedAt);

        Assert.Equal(-1, calibration.SteeringSign, "steering sign corrected");
        Assert.True(calibration.SteeringSignVerified, "verified");
        Assert.True(h.Estimator.CalibrationChanged, "CalibrationChanged");
        Assert.True(h.Log.Contains("steering"), "logged");
        Assert.True(calibratedAt - firstTurnIn >= 199 * Dt, "decided after >= 200 samples: " + F(calibratedAt - firstTurnIn) + " s");
        Assert.Equal(-1, h.Outputs.SteeringSign);
        PhaseStats last = h.Phase("corner4");
        Assert.True(last.MaxUs < 0.05 && last.MaxOs < 0.05, "normal corner after the correction: US " + F(last.MaxUs) + ", OS " + F(last.MaxOs));

        h.Estimator.ClearCalibrationChanged();
        Assert.False(h.Estimator.CalibrationChanged);
    }

    [Test]
    public void SteeringSign_IsConfirmedWhenAdapterIsCorrect()
    {
        var calibration = new SimCalibration { ForwardSignVerified = true, GravityIncluded = true };
        Harness h = RunGentleCorners(calibration, invertSteering: false, out _, out _);
        Assert.Equal(1, calibration.SteeringSign);
        Assert.True(calibration.SteeringSignVerified, "verified");
        Assert.True(h.Estimator.CalibrationChanged, "CalibrationChanged");
        Assert.False(h.Log.Contains("inverted"), "no correction logged");
    }

    [Test]
    public void SteeringSign_InvertedAdapterGivesNoOutputsUntilCorrected()
    {
        // Owner requirement: the first drive in a sim never produces false outputs. The corners run at 7 m/s²,
        // above the linear learning region (like a fast circuit or an oval), with an inverted adapter sign.
        var car = new SimCar();
        var calibration = new SimCalibration { ForwardSignVerified = true, GravityIncluded = true };
        SimScript script = WarmUp(30.0);
        for (int i = 0; i < 6; i++)
        {
            double direction = i % 2 == 0 ? 1.0 : -1.0;
            script.Steer(0.6, direction * car.SteerDegFor(7.0, 30.0), "turn-in" + i);
            script.Hold(2.5, "corner" + i);
            script.Steer(0.6, 0.0, "exit" + i);
            script.Hold(2.0, "straight" + i);
        }

        script.Steer(0.4, 60.0, "turn-in-us");
        script.Steer(1.0, 110.0, "saturating");
        script.Hold(1.5, "understeer");

        bool calibratingSeen = false;
        bool activeBeforeFlip = false;
        double maxBeforeFlip = 0.0;
        int countersteerBeforeFlip = 0;
        Harness h = Run(car, script, SimNoise.Typical(), calibration: calibration, options: new SimOptions { InvertSteering = true },
            onTick: (sim, o) =>
            {
                if (calibration.SteeringSignVerified)
                {
                    return;
                }

                calibratingSeen |= o.Gate == BalanceGate.Calibrating;
                activeBeforeFlip |= o.Active;
                maxBeforeFlip = Math.Max(maxBeforeFlip, Math.Max(o.Understeer, o.Oversteer));
                countersteerBeforeFlip += o.Countersteer ? 1 : 0;
            });

        Assert.True(calibration.SteeringSignVerified, "verified from corners at 7 m/s²");
        Assert.Equal(-1, calibration.SteeringSign, "corrected");
        Assert.True(calibratingSeen, "Calibrating gate reported before the flip");
        Assert.False(activeBeforeFlip, "calibrating is not Active");
        Assert.Equal(0.0, maxBeforeFlip, "no US/OS before the sign is verified");
        Assert.Equal(0, countersteerBeforeFlip, "no countersteer tag before the sign is verified");
        Assert.True(h.Phase("corner0").Gates.Contains(BalanceGate.Calibrating), "first corner is calibrating");
        foreach (string phase in new[] { "corner3", "corner4", "corner5" })
        {
            PhaseStats corner = h.Phase(phase);
            Assert.True(corner.MaxUs < 0.05 && corner.MaxOs < 0.05, phase + " after the flip: US " + F(corner.MaxUs) + ", OS " + F(corner.MaxOs));
            Assert.Equal(0, corner.CountersteerTicks, phase + " countersteer");
            Assert.Equal(BalanceGate.Active, corner.LastGate, phase + " gate");
        }

        Assert.True(h.Phase("understeer").LastUs > 0.5, "understeer detected after the flip: " + F(h.Phase("understeer").LastUs));
    }

    [Test]
    public void SteeringSign_CountersteerDriftDoesNotFlipACorrectSign()
    {
        // Low-ay drifts with the wheel countersteered (dirt or drift session) before the sign is verified: every
        // drift tick has steering against the yaw rate. Those ticks must not vote.
        var car = new SimCar();
        var calibration = new SimCalibration { ForwardSignVerified = true, GravityIncluded = true };
        var script = new SimScript(0.0);
        script.Drive(3.0, 16.0, "warm-up");
        script.Hold(3.0, "warm-up");
        double pathSteer = car.SteerDegFor(3.0, 16.0);
        script.Steer(0.8, pathSteer, "entry");
        script.Hold(1.0, "entry");
        for (int i = 0; i < 4; i++)
        {
            script.SlideTo(1.0, 0.5, "slide" + i).HoldPath = true;
            script.Steer(0.8, -12.0, "countersteer" + i).HoldPath = true;
            script.Hold(3.0, "drift" + i).HoldPath = true;
            script.SlideTo(1.0, 0.0, "recover" + i).HoldPath = true;
            script.Steer(0.8, pathSteer, "resteer" + i);
            script.Hold(1.0, "resteer" + i);
        }

        script.Steer(1.0, 0.0, "straight");
        script.Drive(3.0, 30.0, "straight");
        for (int i = 0; i < 3; i++)
        {
            script.Steer(0.6, car.SteerDegFor(8.0, 30.0) * (i % 2 == 0 ? 1.0 : -1.0), "corner-in" + i);
            script.Hold(2.5, "corner" + i);
            script.Steer(0.6, 0.0, "corner-out" + i);
        }

        Harness h = Run(car, script, SimNoise.Typical(), calibration: calibration);
        Assert.Equal(1, calibration.SteeringSign, "correct sign kept");
        Assert.True(calibration.SteeringSignVerified, "verified by the normal corners");
        Assert.False(h.Log.Contains("inverted"), "no correction logged");
        PhaseStats last = h.Phase("corner2");
        Assert.Equal(0, last.CountersteerTicks, "no countersteer in a normal corner");
        Assert.True(last.MaxOs < 0.05, "no oversteer in a normal corner: " + F(last.MaxOs));
    }

    [Test]
    public void SteeringSign_MonitorReopensAVerificationThatCleanCornersContradict()
    {
        // Verified, but with the adapter sign inverted (e.g. verified in slides on a sim without lateral velocity).
        var car = new SimCar();
        var calibration = VerifiedCalibration();
        SimScript script = WarmUp(30.0);
        for (int i = 0; i < 12; i++)
        {
            script.Steer(0.6, car.SteerDegFor(6.0, 30.0) * (i % 2 == 0 ? 1.0 : -1.0), "turn-in" + i);
            script.Hold(2.5, "corner" + i);
            script.Steer(0.6, 0.0, "exit" + i);
            script.Hold(1.0, "straight" + i);
        }

        bool reopened = false;
        Harness h = Run(car, script, SimNoise.Typical(), calibration: calibration, options: new SimOptions { InvertSteering = true },
            onTick: (sim, o) => reopened |= !calibration.SteeringSignVerified);

        Assert.True(reopened, "verification reopened");
        Assert.True(h.Log.Contains("verifying it again"), "logged");
        Assert.True(calibration.SteeringSignVerified, "verified again");
        Assert.Equal(-1, calibration.SteeringSign, "corrected");
        PhaseStats last = h.Phase("corner11");
        Assert.True(last.MaxUs < 0.05 && last.MaxOs < 0.05, "normal corner after the correction: US " + F(last.MaxUs) + ", OS " + F(last.MaxOs));
        Assert.Equal(0, last.CountersteerTicks, "no countersteer after the correction");
    }

    [Test]
    public void ResetCalibration_VerifiesTheSignsAgainAndKeepsTheirValues()
    {
        var calibration = new SimCalibration { SteeringSign = -1, SteeringSignVerified = true, ForwardSignVerified = true, GravityIncluded = true };
        var estimator = new BalanceEstimator(new BalanceTuning(), NullLog.Instance);
        estimator.LoadCar(NewProfile(), calibration, string.Empty);
        Assert.True(estimator.Outputs.SteeringSignVerified, "published as verified");

        estimator.ResetCalibration();
        Assert.False(calibration.SteeringSignVerified, "steering sign unverified");
        Assert.False(calibration.ForwardSignVerified, "forward sign unverified");
        Assert.Equal(-1, calibration.SteeringSign, "sign value kept");
        Assert.Equal<bool?>(true, calibration.GravityIncluded, "gravity convention kept");
        Assert.True(estimator.CalibrationChanged, "CalibrationChanged");
        Assert.False(estimator.Outputs.SteeringSignVerified, "published as unverified");

        // Driving again confirms the kept value (the adapter is inverted, so -1 is correct).
        Harness h = RunGentleCorners(calibration, invertSteering: true, out _, out _);
        Assert.True(calibration.SteeringSignVerified && calibration.ForwardSignVerified, "verified again");
        Assert.Equal(-1, calibration.SteeringSign, "confirmed");
        Assert.False(h.Log.Contains("inverted"), "no correction");
    }

    [Test]
    public void ForwardSign_IsCorrectedWhenAdapterIsInverted()
    {
        var calibration = new SimCalibration { SteeringSignVerified = true, GravityIncluded = true };
        SimScript script = new SimScript(10.0);
        script.Drive(3.0, 30.0, "accelerating");
        script.Hold(3.0, "cruise");
        script.Steer(1.0, 110.0, "saturating");
        script.Hold(1.0, "understeer");

        bool reverseBeforeFlip = false;
        Harness h = Run(new SimCar(), script, SimNoise.Typical(), calibration: calibration, options: new SimOptions { InvertForward = true },
            onTick: (sim, o) => reverseBeforeFlip |= !calibration.ForwardSignVerified && o.Gate == BalanceGate.Reverse);

        Assert.Equal(-1, calibration.ForwardSign, "forward sign corrected");
        Assert.True(calibration.ForwardSignVerified, "verified");
        Assert.True(h.Estimator.CalibrationChanged, "CalibrationChanged");
        Assert.True(h.Log.Contains("inverted"), "logged");
        Assert.True(reverseBeforeFlip, "inverted velocity reads as reverse until corrected");
        Assert.Equal(BalanceGate.Active, h.Phase("cruise").LastGate);
        Assert.True(h.Phase("understeer").LastUs > 0.5, "works after the correction");
    }

    // =====================================================================================================
    // Estimation paths
    // =====================================================================================================

    [Test]
    public void AutoMode_UsesDirectPathOnceSlipAnglesAndPeakAreLearned()
    {
        var car = new SimCar();
        SimScript script = SkidpadScript(car);
        script.Hold(2.0, "normal");
        script.Steer(1.0, 110.0, "saturating");
        script.Hold(1.5, "understeer");
        script.Hold(1.5, "no-slip-angles").SlipAnglesAvailable = false;

        Harness h = Run(car, script, SimNoise.Typical(), options: new SimOptions { ReportSlipAngles = true });
        Assert.True(h.Phase("skidpad").Paths.Contains(BalancePath.Model), "model path before the peak is learned");
        Assert.True(MathUtil.IsFinite(h.Outputs.AlphaPeak), "alpha peak learned");
        Assert.Equal(BalancePath.Direct, h.Phase("normal").LastPath, "direct path once learned");
        Assert.True(h.Phase("normal").MaxUs < 0.1, "neutral car at the limit: " + F(h.Phase("normal").MaxUs));
        Assert.Equal(BalancePath.Direct, h.Phase("understeer").LastPath);
        Assert.True(h.Phase("understeer").LastUs > 0.5, "direct understeer: " + F(h.Phase("understeer").LastUs));
        Assert.Equal(BalancePath.Model, h.Phase("no-slip-angles").LastPath, "model path without slip angles");
        Assert.True(h.Phase("no-slip-angles").LastUs > 0.5, "model path still detects understeer");
    }

    [Test]
    public void ModelOnly_IgnoresSlipAngles()
    {
        var car = new SimCar();
        SimScript script = SkidpadScript(car);
        script.Hold(2.0, "normal");
        Harness h = Run(car, script, SimNoise.Typical(), tuning: new BalanceTuning { Mode = BalanceMode.ModelOnly },
            options: new SimOptions { ReportSlipAngles = true });
        Assert.True(MathUtil.IsFinite(h.Outputs.AlphaPeak), "alpha peak learned");
        Assert.Equal(BalancePath.Model, h.Phase("normal").LastPath);
    }

    [Test]
    public void DirectOnly_WithoutSlipAnglesHasNoPath()
    {
        SimScript script = UndersteerScript(30.0, out _);
        Harness h = Run(new SimCar(), script, SimNoise.Typical(), tuning: new BalanceTuning { Mode = BalanceMode.DirectOnly });
        PhaseStats understeer = h.Phase("understeer");
        Assert.Equal(0.0, understeer.MaxUs + understeer.MaxOs, "no outputs without the direct path");
        Assert.Equal(BalancePath.None, understeer.LastPath);
        Assert.Equal(BalanceGate.NoData, understeer.LastGate);
        Assert.False(h.Outputs.Active);
        Assert.Equal(0.0, h.Outputs.Confidence);
    }

    // =====================================================================================================
    // Lifecycle, learning and performance
    // =====================================================================================================

    [Test]
    public void Lifecycle_LoadUnloadResetAndSave()
    {
        var tuning = new BalanceTuning();
        var profile = NewProfile();
        var estimator = new BalanceEstimator(tuning, NullLog.Instance);
        Assert.Equal(BalanceGate.NoData, estimator.Outputs.Gate, "no car yet");
        Assert.False(estimator.LearnerDirty);

        var sim = new VehicleSimulator(new SimCar(), LongStraight(40.0, 60.0));
        estimator.Update(Next(sim));
        Assert.Equal(BalanceGate.NoData, estimator.Outputs.Gate, "updates without a car are ignored");

        estimator.LoadCar(profile, VerifiedCalibration(), "GT3");
        Assert.True(MathUtil.IsFinite(estimator.Outputs.G) && estimator.Outputs.G > 0.0, "parameters published on load");
        Assert.Equal(BalanceClassPreset.GT, estimator.AutoClassPreset, "class preset detected from the car class");
        Assert.Equal(BalanceClassPreset.GT, estimator.Outputs.ClassPreset, "detected preset in effect without an override");
        Advance(estimator, sim, 1.0);
        Assert.Equal(BalanceGate.Blanked, estimator.Outputs.Gate, "blanked after LoadCar");
        Advance(estimator, sim, 1.5);
        Assert.Equal(BalanceGate.Active, estimator.Outputs.Gate, "active after the blank");
        Assert.True(estimator.Outputs.SamplesAy > 0, "lateral samples learned");

        estimator.Reset();
        Advance(estimator, sim, 1.0);
        Assert.Equal(BalanceGate.Blanked, estimator.Outputs.Gate, "blanked after Reset");
        Assert.True(estimator.Outputs.SamplesAy > 0, "Reset keeps the learned baseline");

        estimator.SaveTo(profile);
        Assert.False(estimator.LearnerDirty, "SaveTo clears LearnerDirty");

        estimator.ResetLearning();
        Assert.Equal(0L, estimator.Outputs.SamplesAy, "ResetLearning forgets samples");

        estimator.Unload();
        Assert.Equal(BalanceGate.NoData, estimator.Outputs.Gate);
        Assert.Equal(0.0, estimator.Outputs.Understeer + estimator.Outputs.Oversteer);
        Assert.False(estimator.LearnerDirty, "no car, nothing to save");
        Assert.Equal(BalanceClassPreset.None, estimator.AutoClassPreset, "no car, no preset");
        Advance(estimator, sim, 0.5);
        Assert.Equal(BalanceGate.NoData, estimator.Outputs.Gate, "stays idle without a car");

        estimator.LoadCar(null, null, null);
        Assert.Equal(BalanceGate.NoData, estimator.Outputs.Gate, "null profile unloads");
    }

    [Test]
    public void Learning_ConvergesWithinThreeToFiveLapsOfMixedDriving()
    {
        // A car well away from the defaults (G +19 %, K +38 %, 1.5° steering offset, faster yaw response) with an
        // unverified steering sign, as on the very first session in a sim.
        var car = new SimCar { SteeringRatio = 12.0, K = 0.0018, Theta0Deg = 1.5, TauYaw = 0.10 };
        var calibration = new SimCalibration { ForwardSignVerified = true, GravityIncluded = true };
        SimScript script = WarmUp(50.0);
        for (int lap = 1; lap <= 5; lap++)
        {
            AddMixedLap(script, car, "#" + lap);
        }

        double confidenceAfterLap3 = double.NaN;
        double gAfterLap3 = double.NaN;
        Harness h = Run(car, script, SimNoise.Typical(), calibration: calibration, onTick: (sim, outputs) =>
        {
            if (double.IsNaN(confidenceAfterLap3) && sim.Segment.Label == "straight1#4")
            {
                confidenceAfterLap3 = outputs.Confidence;
                gAfterLap3 = outputs.G;
            }
        });

        Assert.True(confidenceAfterLap3 >= 0.7, "confidence after 3 laps: " + F(confidenceAfterLap3));
        Assert.Near(car.G, gAfterLap3, 0.1 * car.G, "G after 3 laps");

        BalanceOutputs o = h.Outputs;
        string summary = "G " + F(o.G) + " (" + o.GSource + ", conf " + F(o.ConfG) + ", n " + o.SamplesG + "), K " + F(o.K, "0.00000")
            + " (" + o.KSource + "), theta0 " + F(o.Theta0Deg) + " (" + o.Theta0Source + "), tau " + F(o.TauYaw) + " (" + o.TauSource + ")";
        Assert.True(calibration.SteeringSignVerified, "steering sign verified: " + summary);
        Assert.Equal(ParamSource.Learned, o.GSource, "G learned: " + summary);
        Assert.True(o.Confidence >= 0.7, "confidence >= 0.7: " + summary);
        Assert.Near(car.G, o.G, 0.1 * car.G, "learned G: " + summary);
        Assert.Equal(ParamSource.Learned, o.Theta0Source, "theta0 learned: " + summary);
        Assert.Near(car.Theta0Deg, o.Theta0Deg, 0.5, "learned theta0: " + summary);
        Assert.Equal(ParamSource.Learned, o.KSource, "K learned: " + summary);
        Assert.Near(car.K, o.K, 0.15 * car.K, "learned K: " + summary);

        // The learned lag also absorbs the filter delays and the discretization of the lag (≈ dt/2 + 1 sample).
        Assert.Equal(ParamSource.Learned, o.TauSource, "tau learned: " + summary);
        Assert.Near(car.TauYaw, o.TauYaw, 0.04, "learned tau: " + summary);

        foreach (string phase in new[] { "gentle1#5", "gentle3#5", "gentle4#5" })
        {
            PhaseStats gentle = h.Phase(phase);
            Assert.True(gentle.MaxUs < 0.05 && gentle.MaxOs < 0.05, phase + ": false outputs after learning " + F(gentle.MaxUs) + " / " + F(gentle.MaxOs));
        }
    }

    [Test]
    public void Learning_PersistsThroughSaveToAndLoadCar()
    {
        var car = new SimCar { SteeringRatio = 12.0, K = 0.0018, Theta0Deg = 1.5 };
        SimScript script = WarmUp(50.0);
        for (int lap = 1; lap <= 3; lap++)
        {
            AddMixedLap(script, car, "#" + lap);
        }

        Harness first = Run(car, script, SimNoise.Typical());
        Assert.Equal(ParamSource.Learned, first.Outputs.GSource, "learned in the first session");
        Assert.True(first.Estimator.LearnerDirty, "unsaved learning");
        double learnedG = first.Outputs.G;
        first.Estimator.SaveTo(first.Profile);
        Assert.False(first.Estimator.LearnerDirty, "saved");

        // Next session: a fresh estimator loads the same profile and starts with the learned model.
        var next = new BalanceEstimator(new BalanceTuning(), NullLog.Instance);
        next.LoadCar(first.Profile, VerifiedCalibration(), string.Empty);
        Assert.Equal(ParamSource.Learned, next.Outputs.GSource, "learned G restored on load");
        Assert.Near(learnedG, next.Outputs.G, 0.01 * learnedG, "restored G");
        Assert.Equal(ParamSource.Learned, next.Outputs.Theta0Source, "learned steering offset restored on load");
        Assert.False(next.LearnerDirty, "nothing to save right after loading");
    }

    [Test]
    public void LearningLock_FreezesTheBaselineUntilUnlocked()
    {
        var car = new SimCar();
        SimScript script = WarmUp(30.0);
        script.Steer(0.6, car.SteerDegFor(3.0, 30.0), "locked");
        script.Hold(5.0, "locked");
        script.Hold(3.0, "unlocked");

        CarProfile profile = null;
        Harness h = Run(car, script, SimNoise.Typical(), profile: p => (profile = p).LearningLocked = true, onTick: (sim, o) =>
        {
            if (sim.Segment.Label == "locked")
            {
                Assert.True(o.LearningLocked, "lock published");
                Assert.Equal(0L, o.SamplesAy, "baseline samples while locked");
            }
            else if (sim.Segment.Label == "unlocked")
            {
                profile.LearningLocked = false; // the shell edits the profile on the data thread; picked up next tick
            }
        });

        Assert.False(h.Outputs.LearningLocked, "unlock published");
        Assert.True(h.Outputs.SamplesAy > 0, "learning resumes after unlocking");
        Assert.True(h.Estimator.LearnerDirty, "baseline changed after unlocking");
    }

    [Test]
    public void WetTrack_DetectsButDoesNotLearn()
    {
        // Spec 5.2: no learning on a wet track (wetness above the threshold or a wet surface); detection still works.
        foreach (bool viaWetness in new[] { true, false })
        {
            SimScript script = UndersteerScript(30.0, out SimCar car);
            foreach (SimSegment segment in script.Segments)
            {
                if (viaWetness)
                {
                    segment.Wetness = 0.8;
                }
                else
                {
                    segment.Surface = SurfaceKind.Wet;
                }
            }

            string mode = viaWetness ? "wetness" : "wet surface";
            Harness h = Run(car, script, SimNoise.Typical(), onTick: (sim, o) => Assert.False(o.LearningActive, mode + ": learning"));
            Assert.Equal(0L, h.Outputs.SamplesAy, mode + ": baseline samples");
            Assert.True(h.Phase("understeer").LastUs > 0.5, mode + ": understeer still detected");
        }
    }

    [Test]
    public void Damage_SessionLayerAdoptsBentSteering()
    {
        // Spec 5.3: after an impact bends the steering, the session layer takes over the offset; the corners are
        // quiet again once it has, although the persisted baseline still describes the undamaged car.
        var car = new SimCar();
        SimScript script = WarmUp(50.0);
        AddMixedLap(script, car, "#1");
        AddMixedLap(script, car, "#2");
        script.Hold(1.0, "hit").CollisionG = 5.0;
        AddMixedLap(script, car, "#3");
        AddMixedLap(script, car, "#4");

        const double BentOffsetDeg = 3.0;
        Harness h = Run(car, script, SimNoise.Typical(), onTick: (sim, o) =>
        {
            if (sim.Segment.Label == "hit")
            {
                car.Theta0Deg = BentOffsetDeg;
            }
        });

        Assert.True(h.Outputs.SessionOverrideActive, "session override active");
        Assert.Equal(ParamSource.Session, h.Outputs.Theta0Source, "offset from the session layer");
        Assert.Near(BentOffsetDeg, h.Outputs.Theta0Deg, 0.5, "session offset follows the bent steering");
        foreach (string phase in new[] { "gentle1#4", "gentle2#4", "gentle3#4", "gentle4#4" })
        {
            PhaseStats gentle = h.Phase(phase);
            Assert.True(gentle.MaxUs < 0.05 && gentle.MaxOs < 0.05, phase + ": outputs with the adopted offset " + F(gentle.MaxUs) + " / " + F(gentle.MaxOs));
        }

        // A new session (car repaired) restarts the session layer: the baseline is back in charge.
        h.Estimator.Reset();
        Assert.False(h.Outputs.SessionOverrideActive, "session layer restarted by Reset");
        Assert.Equal(ParamSource.Learned, h.Outputs.Theta0Source, "baseline offset after Reset");
    }

    [Test]
    public void Learning_ModelSamplesWaitForSteeringOffsetAndSettledYaw()
    {
        // Spec 5.2: θ0 is fitted on straights first, and G/K samples are quasi-steady. Below StraightMinSpeed no
        // offset can be learned, so a linear corner there must not feed the model; neither may a slow steering ramp,
        // whose yaw rate still lags the steering.
        var car = new SimCar { Theta0Deg = 1.0 };
        SimScript script = WarmUp(15.0);
        script.Steer(0.8, car.SteerDegFor(3.0, 15.0), "corner-no-offset");
        script.Hold(4.0, "corner-no-offset");
        script.Steer(0.8, 0.0, "unwind");
        script.Drive(2.0, 30.0, "straight");
        script.Hold(5.0, "straight");
        script.Steer(1.0, car.SteerDegFor(3.0, 30.0), "slow-turn-in");
        script.Hold(4.0, "corner");

        long samplesAtRampStart = -1;
        long samplesAtRampEnd = -1;
        Harness h = Run(car, script, SimNoise.Typical(), onTick: (sim, o) =>
        {
            if (sim.Segment.Label == "corner-no-offset")
            {
                Assert.Equal(ParamSource.Default, o.Theta0Source, "no offset learned below StraightMinSpeed");
                Assert.Equal(0L, o.SamplesG, "model samples before the steering offset is known");
            }

            if (sim.Segment.Label == "slow-turn-in")
            {
                if (samplesAtRampStart < 0)
                {
                    samplesAtRampStart = o.SamplesG;
                }

                samplesAtRampEnd = o.SamplesG;
            }
        });

        Assert.Equal(ParamSource.Learned, h.Phase("corner").LastTheta0Source, "offset learned on the straight");
        Assert.Equal(samplesAtRampStart, samplesAtRampEnd, "model samples while the yaw rate still lags the steering ramp");
        Assert.True(h.Outputs.SamplesG > samplesAtRampEnd + 100, "model samples in the settled corner: " + h.Outputs.SamplesG);
    }

    [Test]
    public void Update_IsAllocationFreeAfterWarmUp()
    {
        const int WarmUpTicks = 6000;
        const int MeasuredTicks = 10000;
        const long AllowanceBytes = 8 * 1024;

        var car = new SimCar();
        var calibration = new SimCalibration();
        SimScript script = WarmUp(30.0);
        for (int i = 0; i < 12; i++)
        {
            AddStressLap(script, car);
        }

        var estimator = new BalanceEstimator(new BalanceTuning(), NullLog.Instance);
        estimator.LoadCar(NewProfile(), calibration, string.Empty);
        var sim = new VehicleSimulator(car, script, SimNoise.Typical(), new SimOptions { ReportSlipAngles = true });
        for (int i = 0; i < WarmUpTicks; i++)
        {
            Assert.True(sim.Step(), "warm-up script long enough");
            estimator.Update(sim.State);
        }

        Assert.True(calibration.SteeringSignVerified && calibration.GravityIncluded.HasValue, "calibration settled during warm-up");
        AppDomain.MonitoringIsEnabled = true;
        long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        int measured = 0;
        for (; measured < MeasuredTicks && sim.Step(); measured++)
        {
            estimator.Update(sim.State);
        }

        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        Assert.Equal(MeasuredTicks, measured, "measured ticks");
        Assert.True(allocated <= AllowanceBytes, "allocated " + allocated + " bytes in " + MeasuredTicks + " updates");
    }

    // =====================================================================================================
    // Scenario building blocks
    // =====================================================================================================

    private static SimScript WarmUp(double speed)
    {
        var script = new SimScript(speed);
        script.Hold(WarmUpSeconds, "warm-up");
        return script;
    }

    private static SimScript LongStraight(double speed, double seconds)
    {
        var script = new SimScript(speed);
        script.Hold(seconds, "straight");
        return script;
    }

    /// <summary>Warm-up, turn-in below the limit, then steering well past the front grip limit (US ≈ 0.6).</summary>
    private static SimScript UndersteerScript(double speed, out SimCar car)
    {
        car = new SimCar();
        SimScript script = WarmUp(speed);
        script.Steer(0.4, 60.0, "turn-in");
        script.Steer(1.0, 110.0, "saturating");
        script.Hold(1.5, "understeer");
        return script;
    }

    /// <summary>Warm-up and a long constant-radius corner close to the limit (learns ay_max and the slip-angle peak).</summary>
    private static SimScript SkidpadScript(SimCar car)
    {
        SimScript script = WarmUp(30.0);
        script.Steer(0.5, car.SteerDegFor(12.0, 30.0), "skidpad");
        script.Hold(45.0, "skidpad");
        return script;
    }

    private static Harness RunRearSlide(double throttle)
    {
        var car = new SimCar();
        SimScript script = WarmUp(30.0);
        script.Steer(0.4, car.SteerDegFor(10.0, 30.0), "turn-in");
        script.Hold(1.0, "corner").Pedals(throttle, 0.0);
        script.SlideTo(0.4, 0.6, "slide").Pedals(throttle, 0.0);
        script.Hold(0.4, "sliding").Pedals(throttle, 0.0);
        return Run(car, script, SimNoise.Typical());
    }

    private static Harness RunGentleCorners(SimCalibration calibration, bool invertSteering, out double firstTurnIn, out double calibratedAt)
    {
        var car = new SimCar();
        double speed = 25.0;
        double steer = car.SteerDegFor(3.0, speed);
        SimScript script = WarmUp(speed);
        for (int i = 1; i <= 4; i++)
        {
            double direction = i % 2 == 0 ? -1.0 : 1.0;
            script.Steer(0.8, direction * steer, "turn-in" + i);
            script.Hold(5.0, "corner" + i);
            script.Steer(0.8, 0.0, "unwind" + i);
            script.Hold(1.0, "straight" + i);
        }

        double turnIn = double.NaN;
        double decided = double.NaN;
        Harness h = Run(car, script, SimNoise.Typical(), calibration: calibration, options: new SimOptions { InvertSteering = invertSteering },
            onTick: (sim, o) =>
            {
                if (double.IsNaN(turnIn) && sim.Segment.Label == "turn-in1")
                {
                    turnIn = sim.Time;
                }

                if (double.IsNaN(decided) && calibration.SteeringSignVerified)
                {
                    decided = sim.Time;
                }
            });
        firstTurnIn = turnIn;
        calibratedAt = decided;
        return h;
    }

    /// <summary>
    /// One lap (~65 s) of straights, braking, fast turn-ins, gentle corners at 25-55 m/s, a hairpin and a chicane.
    /// <paramref name="suffix"/> is appended to every label so a particular lap can be inspected on its own.
    /// </summary>
    private static void AddMixedLap(SimScript s, SimCar car, string suffix)
    {
        s.Drive(6.0, 50.0, "straight1" + suffix);
        s.Drive(2.5, 30.0, "brake1" + suffix);
        s.Steer(0.35, car.SteerDegFor(8.0, 30.0), "turn-in1" + suffix);
        s.Hold(2.5, "corner1" + suffix);
        s.Steer(0.4, 0.0, "unwind1" + suffix);
        s.Drive(3.0, 45.0, "accel1" + suffix);
        s.Steer(0.6, car.SteerDegFor(3.0, 45.0), "gentle1-in" + suffix);
        s.Hold(5.0, "gentle1" + suffix);
        s.Steer(0.6, 0.0, "gentle1-out" + suffix);
        s.Hold(2.0, "straight2" + suffix);
        s.Steer(0.6, -car.SteerDegFor(3.5, 45.0), "gentle2-in" + suffix);
        s.Hold(4.0, "gentle2" + suffix);
        s.Steer(0.6, 0.0, "gentle2-out" + suffix);
        s.Drive(3.0, 22.0, "brake2" + suffix);
        s.Steer(0.4, -car.SteerDegFor(12.0, 22.0), "hairpin-in" + suffix);
        s.Hold(2.0, "hairpin" + suffix);
        s.Steer(0.5, 0.0, "hairpin-out" + suffix);
        s.Drive(1.0, 25.0, "accel2" + suffix);
        s.Steer(0.5, car.SteerDegFor(2.5, 25.0), "gentle3-in" + suffix);
        s.Hold(5.0, "gentle3" + suffix);
        s.Steer(0.5, 0.0, "gentle3-out" + suffix);
        s.Drive(5.0, 55.0, "accel3" + suffix);
        s.Steer(0.8, -car.SteerDegFor(3.5, 55.0), "gentle4-in" + suffix);
        s.Hold(5.0, "gentle4" + suffix);
        s.Steer(0.8, 0.0, "gentle4-out" + suffix);
        s.Hold(4.0, "straight3" + suffix);
        s.Drive(2.5, 35.0, "brake3" + suffix);
        s.Steer(0.35, car.SteerDegFor(6.0, 35.0), "chicane-in" + suffix);
        s.Hold(0.8, "chicane-left" + suffix);
        s.Steer(0.5, -car.SteerDegFor(6.0, 35.0), "chicane-switch" + suffix);
        s.Hold(0.8, "chicane-right" + suffix);
        s.Steer(0.4, 0.0, "chicane-out" + suffix);
        s.Drive(3.0, 50.0, "accel4" + suffix);
    }

    /// <summary>One lap (~40 s) exercising every estimator code path: gates, blanks, detectors, direct path, learning.</summary>
    private static void AddStressLap(SimScript s, SimCar car)
    {
        s.Drive(1.0, 30.0, "straight");
        s.Steer(0.35, car.SteerDegFor(12.0, 30.0), "turn-in");
        s.Hold(2.0, "corner");
        s.Steer(0.8, 110.0, "saturating");
        s.Hold(1.0, "understeer").Pedals(0.0, 0.3);
        s.Hold(0.5, "hit").CollisionG = 5.0;
        s.Steer(0.5, car.SteerDegFor(9.0, 30.0), "corner");
        s.Hold(1.5, "kerbs").With(x =>
        {
            x.KerbSpikeInterval = 0.25;
            x.Surface = SurfaceKind.Kerb;
        });
        s.SlideTo(0.4, 0.8, "slide").Pedals(1.0, 0.0);
        s.To(0.3, -25.0, 30.0, "counter", slideYaw: 0.3).HoldPath = true;
        s.Hold(0.8, "held").HoldPath = true;
        s.To(0.8, 0.0, 30.0, "recover", slideYaw: 0.0);
        s.Hold(0.2, "air").Airborne = true;
        s.Hold(1.0, "straight");
        s.Hold(0.4, "frozen").Frozen = true;
        s.Hold(1.0, "pit").PitLane = true;
        s.Hold(0.5, "invalid").Invalid = true;
        s.Steer(0.4, car.SteerDegFor(6.0, 30.0), "turn-in");
        s.SlideTo(0.5, 2.4, "spin").HoldPath = true;
        s.Hold(2.0, "spinning").HoldPath = true;
        s.To(1.0, 0.0, 30.0, "recover", slideYaw: 0.0);
        s.Hold(1.0, "slip-off").SlipAnglesAvailable = false;
        s.Steer(0.6, car.SteerDegFor(3.0, 30.0), "gentle");
        s.Hold(3.0, "gentle");
        s.Steer(0.6, 0.0, "gentle");
        s.Hold(1.0, "replay").Replay = true;
        s.Hold(2.5, "straight");
    }

    private static void AssertContactBlank(Action<SimSegment> configureHit)
    {
        var tuning = new BalanceTuning();
        SimScript script = UndersteerScript(30.0, out _);
        script.Hold(2.0, "hit").With(configureHit);

        bool contactGate = false;
        double atBlankEnd = double.NaN;
        Harness h = Run(new SimCar(), script, SimNoise.Typical(), tuning: tuning, onTick: (sim, o) =>
        {
            if (sim.Segment.Label != "hit")
            {
                return;
            }

            if (sim.SegmentTime < tuning.ContactBlankTime - 0.02)
            {
                contactGate |= o.Gate == BalanceGate.Contact;
                Assert.False(o.Active, "active during the contact blank");
            }

            if (double.IsNaN(atBlankEnd) && sim.SegmentTime >= tuning.ContactBlankTime - 0.05)
            {
                atBlankEnd = o.Understeer;
            }
        });

        Assert.True(h.Phase("understeer").LastUs > 0.5, "understeer before the hit");
        Assert.True(contactGate, "Contact gate");
        Assert.True(atBlankEnd < 0.1, "faded during the blank: " + F(atBlankEnd));
        Assert.True(h.Phase("hit").LastUs > 0.5, "recovers after the blank: " + F(h.Phase("hit").LastUs));
    }

    private static void AssertAirborneFade(bool gravityIncluded)
    {
        var calibration = new SimCalibration { SteeringSignVerified = true, ForwardSignVerified = true };
        var tuning = new BalanceTuning();
        SimScript script = UndersteerScript(30.0, out _);
        script.Hold(0.25, "air").Airborne = true;
        script.Hold(0.25, "landing");
        script.Hold(1.5, "after");

        bool airborneGate = false;
        Harness h = Run(new SimCar(), script, SimNoise.Typical(), tuning: tuning, calibration: calibration,
            options: new SimOptions { GravityIncluded = gravityIncluded },
            onTick: (sim, o) => airborneGate |= o.Gate == BalanceGate.Airborne);

        Assert.Equal<bool?>(gravityIncluded, calibration.GravityIncluded, "gravity convention detected");
        Assert.Equal<bool?>(gravityIncluded, h.Outputs.GravityIncluded, "published");
        Assert.True(h.Estimator.CalibrationChanged, "CalibrationChanged");
        Assert.False(h.Phase("understeer").Gates.Contains(BalanceGate.Airborne), "no false airborne before the jump");
        Assert.True(airborneGate, "Airborne gate");
        Assert.True(h.Phase("landing").MaxUs < 0.3, "faded while airborne: " + F(h.Phase("landing").MaxUs));
        Assert.True(h.Phase("after").LastUs > 0.5, "recovers after landing: " + F(h.Phase("after").LastUs));
    }

    private static Harness AssertBlankedThenActive(SimScript script, string phase, BalanceTuning tuning, Action<VehicleSimulator, BalanceOutputs> onTick = null)
    {
        bool blankedEarly = true;
        bool activeLate = false;
        Harness h = Run(new SimCar(), script, SimNoise.Typical(), tuning: tuning, onTick: (sim, o) =>
        {
            onTick?.Invoke(sim, o);
            if (sim.Segment.Label != phase)
            {
                return;
            }

            if (sim.SegmentTime > 0.1 && sim.SegmentTime < tuning.ResetBlankTime - 0.1)
            {
                blankedEarly &= o.Gate == BalanceGate.Blanked;
            }

            if (sim.SegmentTime > tuning.ResetBlankTime + 0.2)
            {
                activeLate |= o.Gate == BalanceGate.Active;
            }
        });

        Assert.True(blankedEarly, "blanked for ResetBlankTime after the discontinuity");
        Assert.True(activeLate, "active again after the blank");
        return h;
    }

    // =====================================================================================================
    // Harness
    // =====================================================================================================

    private static Harness Run(
        SimCar car,
        SimScript script,
        SimNoise noise = null,
        BalanceTuning tuning = null,
        SimCalibration calibration = null,
        SimOptions options = null,
        Action<CarProfile> profile = null,
        Action<VehicleSimulator, BalanceOutputs> onTick = null)
    {
        var harness = new Harness(tuning ?? new BalanceTuning(), calibration ?? VerifiedCalibration(), profile);
        harness.Run(new VehicleSimulator(car, script, noise, options), onTick);
        return harness;
    }

    private static SimCalibration VerifiedCalibration() =>
        new SimCalibration { SteeringSignVerified = true, ForwardSignVerified = true, GravityIncluded = true };

    private static CarProfile NewProfile() =>
        new CarProfile { SimKey = "UnitTest", CarKey = "UnitTestCar", DisplayName = "Unit test car" };

    /// <summary>Manual G and K equal to the simulated car, so the model is exact (deterministic threshold tests).</summary>
    private static void ExactModel(CarProfile profile, SimCar car)
    {
        profile.Overrides.G = car.G;
        profile.Overrides.K = car.K;
        profile.Overrides.TauYawS = car.TauYaw;
    }

    /// <summary>Steering angle (deg) at which the steady-state yaw deficit 1 - r/r_ss equals <paramref name="understeer"/>.</summary>
    private static double SteerDegForUndersteer(SimCar car, double understeer, double speed)
    {
        double yawCap = car.FrontGripLimit / speed;
        double yawDemand = yawCap / (1.0 - understeer);
        return yawDemand * (1.0 + (car.K * speed * speed)) / (car.G * speed) * MathUtil.RadToDeg;
    }

    private static VehicleState Next(VehicleSimulator sim)
    {
        Assert.True(sim.Step(), "simulator script ended");
        return sim.State;
    }

    private static void Advance(BalanceEstimator estimator, VehicleSimulator sim, double seconds)
    {
        int ticks = (int)Math.Round(seconds / Dt);
        for (int i = 0; i < ticks; i++)
        {
            estimator.Update(Next(sim));
        }
    }

    private static string F(double value, string format = "0.000") => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Estimator + car profile + calibration, with per-phase statistics of the outputs.</summary>
    private sealed class Harness
    {
        private readonly Dictionary<string, PhaseStats> phases = new Dictionary<string, PhaseStats>(StringComparer.Ordinal);

        public Harness(BalanceTuning tuning, SimCalibration calibration, Action<CarProfile> configureProfile)
        {
            Profile = NewProfile();
            configureProfile?.Invoke(Profile);
            Estimator = new BalanceEstimator(tuning, Log);
            Estimator.LoadCar(Profile, calibration, string.Empty);
        }

        public CarProfile Profile { get; }

        public RecordingLog Log { get; } = new RecordingLog();

        public BalanceEstimator Estimator { get; }

        public BalanceOutputs Outputs => Estimator.Outputs;

        public void Run(VehicleSimulator sim, Action<VehicleSimulator, BalanceOutputs> onTick)
        {
            while (sim.Step())
            {
                Estimator.Update(sim.State);
                string label = sim.Segment.Label;
                if (!phases.TryGetValue(label, out PhaseStats stats))
                {
                    stats = new PhaseStats();
                    phases.Add(label, stats);
                }

                stats.Add(Estimator.Outputs);
                onTick?.Invoke(sim, Estimator.Outputs);
            }
        }

        public PhaseStats Phase(string label)
        {
            if (!phases.TryGetValue(label, out PhaseStats stats))
            {
                throw new AssertionException("scenario has no phase '" + label + "'");
            }

            return stats;
        }
    }

    /// <summary>Output statistics over all ticks of one scenario phase (segments sharing a label).</summary>
    private sealed class PhaseStats
    {
        public readonly HashSet<BalanceGate> Gates = new HashSet<BalanceGate>();
        public readonly HashSet<BalancePath> Paths = new HashSet<BalancePath>();

        public int Ticks;
        public int ActiveTicks;
        public double MaxUs;
        public double MaxOs;
        public double MinUs = double.MaxValue;
        public double MinOs = double.MaxValue;
        public double LastUs;
        public double LastOs;
        public double MaxBodySlipDeg;
        public BalanceGate LastGate;
        public BalancePath LastPath;
        public ParamSource LastTheta0Source;
        public int PowerTicks;
        public int LiftTicks;
        public int EntryTicks;
        public int ExitTicks;
        public int CountersteerTicks;
        public int SpinTicks;

        public void Add(BalanceOutputs o)
        {
            Ticks++;
            ActiveTicks += o.Active ? 1 : 0;
            MaxUs = Math.Max(MaxUs, o.Understeer);
            MaxOs = Math.Max(MaxOs, o.Oversteer);
            MinUs = Math.Min(MinUs, o.Understeer);
            MinOs = Math.Min(MinOs, o.Oversteer);
            LastUs = o.Understeer;
            LastOs = o.Oversteer;
            if (MathUtil.IsFinite(o.BodySlipDeg) && Math.Abs(o.BodySlipDeg) > Math.Abs(MaxBodySlipDeg))
            {
                MaxBodySlipDeg = o.BodySlipDeg;
            }

            LastGate = o.Gate;
            LastPath = o.Path;
            LastTheta0Source = o.Theta0Source;
            Gates.Add(o.Gate);
            Paths.Add(o.Path);
            PowerTicks += o.PowerOversteer ? 1 : 0;
            LiftTicks += o.LiftOrBrakeOversteer ? 1 : 0;
            EntryTicks += o.EntryUndersteer ? 1 : 0;
            ExitTicks += o.ExitUndersteer ? 1 : 0;
            CountersteerTicks += o.Countersteer ? 1 : 0;
            SpinTicks += o.Spin ? 1 : 0;
        }
    }

    /// <summary>Captures log messages for assertions.</summary>
    private sealed class RecordingLog : ILog
    {
        private readonly List<string> messages = new List<string>();

        public void Info(string message) => messages.Add(message);

        public void Warn(string message) => messages.Add(message);

        public void Error(string message) => messages.Add(message);

        public bool Contains(string fragment)
        {
            foreach (string message in messages)
            {
                if (message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
