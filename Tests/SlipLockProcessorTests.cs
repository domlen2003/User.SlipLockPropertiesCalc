using System;
using System.Collections.Generic;
using System.Globalization;
using DivebombLogistics.Core;
using DivebombLogistics.Haptics.Settings;
using DivebombLogistics.Haptics.SlipLock;
using DivebombLogistics.Tests.Legacy;

namespace DivebombLogistics.Tests;

/// <summary>
/// <see cref="SlipLockProcessor"/>: bit-exact equivalence with v1 (via <see cref="LegacyPipeline"/>) and the three
/// deliberate v2 changes (A: sensitivity, B: signed sources, C: ShakeIT WheelLock merge).
/// </summary>
internal static class SlipLockProcessorTests
{
    private const int FramesPerSequence = 20000;
    private const double FrameDt = 1.0 / 60.0;

    /// <summary>Every code-defined game preset (by its table key) plus the fallback.</summary>
    private static readonly string[] GamePresetNames =
    {
        "IRacing", "AssettoCorsaCompetizione", "AssettoCorsa", "AssettoCorsaEvo", "LMU", "RFactor2", "AMS2", "BeamNGdrive", GamePresets.DefaultName,
    };

    /// <summary>v1 property names per channel and wheel (precomputed so the comparison loop stays fast).</summary>
    private static readonly string[] SlipKeys = PropertyNames("Slip");
    private static readonly string[] LockKeys = PropertyNames("Lock");
    private static readonly string[] ABSKeys = PropertyNames("ABS");
    private static readonly string[] TCKeys = PropertyNames("TC");
    private static readonly string[] SlipBlendKeys = PropertyNames("SlipBlend");
    private static readonly string[] LockBlendKeys = PropertyNames("LockBlend");
    private static readonly string[] SlipTCKeys = PropertyNames("SlipTC");
    private static readonly string[] LockABSKeys = PropertyNames("LockABS");

    // ------------------------------------------------------------------------------------------------------------
    // Equivalence with v1
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void MatchesV1BitForBit_GamePresetsPart1() => RunGamePresets(0, GamePresetNames.Length / 2);

    [Test]
    public static void MatchesV1BitForBit_GamePresetsPart2() => RunGamePresets(GamePresetNames.Length / 2, GamePresetNames.Length);

    /// <summary>Branches no shipped preset uses today (no synth lock, pre-gain below 100, fade with plain load).</summary>
    [Test]
    public static void MatchesV1BitForBit_SyntheticPresets()
    {
        var presets = new[]
        {
            new GamePreset(40, 20, 35, 45, 60, 40, 30, 70, synthLockFromSlip: false, speedFadeKmh: 0, inverseSlipLoad: false, preGain: 80, preCut: 0),
            new GamePreset(15, 25, 15, 15, 50, 50, 50, 50, synthLockFromSlip: true, speedFadeKmh: 120, inverseSlipLoad: false, preGain: 70, preCut: 10),
            new GamePreset(100, 100, 100, 100, 100, 100, 100, 100, synthLockFromSlip: false, speedFadeKmh: 50, inverseSlipLoad: true, preGain: 100, preCut: 30),
        };

        for (int p = 0; p < presets.Length; p++)
        {
            RunEquivalenceSequence(presets[p], "synthetic" + p.ToString(CultureInfo.InvariantCulture), seed: 2000 + p);
        }
    }

    /// <summary>A second, differently seeded pass over the LMU preset (the user's main game) with other settings.</summary>
    [Test]
    public static void MatchesV1BitForBit_LmuSecondSeed()
    {
        RunEquivalenceSequence(GamePresets.Get("LMU", out _), "LMU", seed: 3000);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Change A: sensitivity
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void SlipSensitivity_ScalesSlipChannel()
    {
        Assert.Equal(20.0, RunSingleSlip(rawSlip: 20, sensitivity: 1.0), "100 %");
        Assert.Equal(40.0, RunSingleSlip(rawSlip: 20, sensitivity: 2.0), "200 %");
        Assert.Equal(10.0, RunSingleSlip(rawSlip: 20, sensitivity: 0.5), "50 %");
    }

    [Test]
    public static void SlipSensitivity_ClampsAt100()
    {
        var rig = new Rig(GamePresets.Default);
        rig.Tuning.SlipSensitivity = 5.0;
        rig.SetAllBaseSlip(60);
        rig.Input.Throttle = 100;
        rig.Step();

        for (int i = 0; i < Wheels.Count; i++)
        {
            Assert.Equal(100.0, rig.Output.Slip[i], "slip clamped");
            Assert.InRange(rig.Output.SlipBlend[i], 0, 100, "blend in range");
        }

        Assert.Equal(100.0, rig.Output.SlipMono, "mono clamped");
    }

    [Test]
    public static void LockSensitivity_ScalesSynthesizedLock()
    {
        foreach (double sensitivity in new[] { 0.5, 1.0, 3.0 })
        {
            var rig = new Rig(GamePresets.Default);
            rig.Tuning.LockSensitivity = sensitivity;
            rig.SetAllBaseSlip(12);
            rig.Input.Brake = 60;
            rig.Input.Surge = 3;
            rig.Step();

            double[] loads = LockLoads(rig, GamePresets.Default);
            for (int i = 0; i < Wheels.Count; i++)
            {
                double expected = Math.Round(Clamp100(12 * sensitivity * loads[i] / ProxyLoad.Neutral), 1);
                Assert.Equal(expected, rig.Output.Lock[i], "lock wheel " + Wheels.ShortNames[i] + " @" + sensitivity.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    [Test]
    public static void Sensitivity_DoesNotAffectSlipWhenOnlyLockChanges_AndNotAbsTc()
    {
        var reference = new Rig(GamePresets.Default);
        var scaled = new Rig(GamePresets.Default);
        scaled.Tuning.LockSensitivity = 4.0;
        foreach (Rig rig in new[] { reference, scaled })
        {
            rig.SetAllBaseSlip(30);
            rig.Input.Throttle = 40;
            rig.Input.AbsActive = true;
            rig.Input.TcActive = true;
            rig.Input.Sway = 8;
            rig.Step();
        }

        for (int i = 0; i < Wheels.Count; i++)
        {
            Assert.Equal(reference.Output.Slip[i], scaled.Output.Slip[i], "slip unaffected by lock sensitivity");
            Assert.Equal(reference.Output.Abs[i], scaled.Output.Abs[i], "ABS unaffected");
            Assert.Equal(reference.Output.Tc[i], scaled.Output.Tc[i], "TC unaffected");
        }
    }

    [Test]
    public static void Sensitivity100Percent_FromCarProfile_IsExactlyOne()
    {
        // The shell passes profile percent / 100; the default profile must give exactly the v1 factor.
        var profile = new CarProfile();
        Assert.Equal(1.0, profile.SlipSensitivity / 100.0, "slip factor");
        Assert.Equal(1.0, profile.LockSensitivity / 100.0, "lock factor");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Change B: signed sources
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void SignedSource_LockFromNegativeSlip_EvenWhenPresetSynthesizes()
    {
        var rig = new Rig(GamePresets.Default);
        Assert.True(GamePresets.Default.SynthLockFromSlip, "precondition: preset synthesizes lock");
        rig.Input.BaseSlipIsSigned = true;
        rig.SetBaseSlip(-40, -10, 30, 0);
        rig.Step(); // no brake, no surge: v1 synth would give 0 lock

        Assert.Equal(40.0, rig.Output.Lock[Wheels.FrontLeft], "FL lock from -40");
        Assert.Equal(10.0, rig.Output.Lock[Wheels.FrontRight], "FR lock from -10");
        Assert.Equal(0.0, rig.Output.Lock[Wheels.RearLeft], "positive slip is not lock");
        Assert.Equal(0.0, rig.Output.Lock[Wheels.RearRight], "zero slip");
        Assert.Equal(30.0, rig.Output.Slip[Wheels.RearLeft], "positive slip is slip");
        Assert.Equal(0.0, rig.Output.Slip[Wheels.FrontLeft], "negative slip is not slip");
        Assert.False(rig.Processor.LockSynthesizedFromSlip, "signed source does not synthesize");
    }

    [Test]
    public static void SignedSource_PositiveSlipUnderBraking_IsNotLock()
    {
        var signed = new Rig(GamePresets.Default);
        signed.Input.BaseSlipIsSigned = true;
        var unsigned = new Rig(GamePresets.Default);
        foreach (Rig rig in new[] { signed, unsigned })
        {
            rig.SetAllBaseSlip(30);
            rig.Input.Brake = 80;
            rig.Input.Surge = 5;
            rig.Step();
        }

        for (int i = 0; i < Wheels.Count; i++)
        {
            Assert.Equal(0.0, signed.Output.Lock[i], "signed: no lock from positive slip");
            Assert.True(unsigned.Output.Lock[i] > 0, "unsigned: synthesized lock while braking");
        }

        Assert.True(unsigned.Processor.LockSynthesizedFromSlip, "unsigned source synthesizes");
    }

    [Test]
    public static void BaseLockMono_FollowsTheLockRule()
    {
        var rig = new Rig(GamePresets.Default);

        // Unsigned + braking: v1 value (raw FL slip).
        rig.SetBaseSlip(25, 0, 0, 0);
        rig.Input.Brake = 50;
        rig.Input.Surge = 2;
        rig.Step();
        Assert.Equal(25.0, rig.Output.BaseLockMono, "unsigned braking");

        // Unsigned, not braking: 0 (v1).
        rig.Input.Brake = 0;
        rig.Step();
        Assert.Equal(0.0, rig.Output.BaseLockMono, "unsigned not braking");

        // Signed: magnitude of negative FL slip, independent of braking.
        rig.Input.BaseSlipIsSigned = true;
        rig.SetBaseSlip(-25, 0, 0, 0);
        rig.Step();
        Assert.Equal(25.0, rig.Output.BaseLockMono, "signed negative");
        rig.SetBaseSlip(25, 0, 0, 0);
        rig.Input.Brake = 50;
        rig.Step();
        Assert.Equal(0.0, rig.Output.BaseLockMono, "signed positive");
        Assert.Equal(25.0, rig.Output.BaseSlipMono, "base slip mono is raw FL");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Change C: ShakeIT WheelLock merge
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void ShakeItLock_MergedAsMaximum()
    {
        var rig = new Rig(GamePresets.Default);
        rig.Input.HasShakeItLock = true;
        rig.SetShakeItLock(50, 0, 20, 0);
        rig.Input.Brake = 50; // surge 0: synthesized lock is off, only ShakeIT contributes
        rig.Step();

        Assert.Equal(50.0, rig.Output.Lock[Wheels.FrontLeft], "FL ShakeIT lock");
        Assert.Equal(0.0, rig.Output.Lock[Wheels.FrontRight], "FR");
        Assert.Equal(20.0, rig.Output.Lock[Wheels.RearLeft], "RL ShakeIT lock");
        Assert.True(rig.Processor.LockMergedShakeIt, "merge flag");

        // Synthesized lock larger than ShakeIT: the maximum wins per wheel.
        var both = new Rig(GamePresets.Default);
        both.Input.HasShakeItLock = true;
        both.SetShakeItLock(5, 60, 5, 5);
        both.SetAllBaseSlip(30);
        both.Input.Brake = 50;
        both.Input.Surge = 0.2;
        both.Step();
        double[] loads = LockLoads(both, GamePresets.Default);
        Assert.Equal(Math.Round(30 * loads[0] / ProxyLoad.Neutral, 1), both.Output.Lock[Wheels.FrontLeft], "synth wins FL");
        Assert.Equal(Math.Round(60 * loads[1] / ProxyLoad.Neutral, 1), both.Output.Lock[Wheels.FrontRight], "ShakeIT wins FR");
    }

    [Test]
    public static void ShakeItLock_RespectsSensitivityLoadFadeAndPreCut()
    {
        GamePreset lmu = GamePresets.Get("LMU", out _);
        Assert.True(lmu.SpeedFadeKmh > 0 && lmu.PreCut > 0, "precondition: LMU fades and cuts");

        var rig = new Rig(lmu);
        rig.Tuning.LockSensitivity = 2.0;
        rig.Input.HasShakeItLock = true;
        rig.SetShakeItLock(40, 25, 10, 0);
        rig.Input.SpeedKmh = lmu.SpeedFadeKmh / 2; // fade 0.5
        rig.Input.Sway = -9;
        rig.Input.Brake = 70; // surge 0: no synthesized lock
        rig.Step();

        double[] loads = LockLoads(rig, lmu);
        double[] shakeIt = { 40, 25, 10, 0 };
        for (int i = 0; i < Wheels.Count; i++)
        {
            double value = Clamp100(shakeIt[i] * 2.0 * loads[i] / ProxyLoad.Neutral * 0.5);
            value = value <= lmu.PreCut ? 0 : (value - lmu.PreCut) / (100.0 - lmu.PreCut) * 100.0;
            Assert.Equal(Math.Round(Clamp100(value), 1), rig.Output.Lock[i], "wheel " + Wheels.ShortNames[i]);
        }

        Assert.True(rig.Output.Lock[Wheels.FrontLeft] > 0, "FL above the cut");
        Assert.Equal(0.0, rig.Output.Lock[Wheels.RearLeft], "RL below the cut");
    }

    [Test]
    public static void ShakeItLock_InactiveWhenDisabledOrUnavailable()
    {
        var disabled = new Rig(GamePresets.Default);
        disabled.Tuning.UseShakeItWheelLock = false;
        disabled.Input.HasShakeItLock = true;
        disabled.SetShakeItLock(80, 80, 80, 80);

        var unavailable = new Rig(GamePresets.Default);
        unavailable.Input.HasShakeItLock = false;
        unavailable.SetShakeItLock(80, 80, 80, 80);

        foreach (Rig rig in new[] { disabled, unavailable })
        {
            rig.Input.Brake = 50;
            rig.Step();
            for (int i = 0; i < Wheels.Count; i++)
            {
                Assert.Equal(0.0, rig.Output.Lock[i], "no merge");
            }

            Assert.False(rig.Processor.LockMergedShakeIt, "merge flag off");
        }
    }

    [Test]
    public static void ShakeItLock_NonFiniteValuesAreIgnored()
    {
        var rig = new Rig(GamePresets.Default);
        rig.Input.HasShakeItLock = true;
        rig.SetShakeItLock(double.NaN, double.PositiveInfinity, 30, 0);
        rig.Input.Brake = 50;
        rig.Step();

        Assert.Equal(0.0, rig.Output.Lock[Wheels.FrontLeft], "NaN ignored");
        Assert.Equal(0.0, rig.Output.Lock[Wheels.FrontRight], "infinity ignored");
        Assert.Equal(30.0, rig.Output.Lock[Wheels.RearLeft], "finite value used");
        AssertAllFinite(rig.Output);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Robustness and lifecycle
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void NonFiniteTelemetry_DoesNotPoisonEnvelopes()
    {
        var rig = new Rig(GamePresets.Get("LMU", out _));
        rig.SetBaseSlip(double.NaN, 20, double.PositiveInfinity, 10);
        rig.Input.Sway = double.NaN;
        rig.Input.Surge = double.NegativeInfinity;
        rig.Input.SpeedKmh = double.NaN;
        rig.Input.Throttle = double.NaN;
        rig.Input.WallTime = double.NaN;
        rig.Tuning.SlipSensitivity = double.NaN;
        rig.Process();
        AssertAllFinite(rig.Output);

        rig.Input.Sway = 3;
        rig.Input.Surge = 1;
        rig.Input.SpeedKmh = 150;
        rig.Input.Throttle = 50;
        rig.SetAllBaseSlip(40);
        for (int n = 0; n < 50; n++)
        {
            rig.Step();
            AssertAllFinite(rig.Output);
        }

        Assert.True(rig.Output.SlipBlendMono > 0, "recovers after bad frame");
    }

    [Test]
    public static void Reset_ClearsEnvelopesAndFrameClock()
    {
        var used = new Rig(GamePresets.Default);
        used.Tuning.SlipReleaseMs = 2000; // long release: without reset the tail would linger
        used.SetAllBaseSlip(80);
        used.Input.Throttle = 100;
        for (int n = 0; n < 30; n++)
        {
            used.Step();
        }

        used.Processor.Reset();
        Assert.False(used.Processor.LockSynthesizedFromSlip || used.Processor.LockMergedShakeIt, "flags cleared");

        var fresh = new Rig(GamePresets.Default);
        fresh.Tuning.SlipReleaseMs = 2000;
        foreach (Rig rig in new[] { used, fresh })
        {
            rig.SetAllBaseSlip(10);
            rig.Input.Throttle = 100;
            rig.Input.WallTime = 500.0; // arbitrary: the first frame after reset must use the first-frame dt
            rig.Process();
        }

        for (int i = 0; i < Wheels.Count; i++)
        {
            Assert.Equal(fresh.Output.SlipBlend[i], used.Output.SlipBlend[i], "same as a fresh processor");
        }
    }

    [Test]
    public static void Process_IsAllocationFree()
    {
        var rig = new Rig(GamePresets.Get("LMU", out _));
        rig.Input.HasShakeItLock = true;
        rig.Input.GameExportsTc = true;
        rig.Input.TcLevel = 2;
        var random = new Random(7);
        for (int n = 0; n < 200; n++)
        {
            RandomizeFrame(random, rig.Input);
            rig.Step();
        }

        AppDomain.MonitoringIsEnabled = true;
        long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        for (int n = 0; n < 20000; n++)
        {
            rig.Input.Sway = (n % 50) - 25;
            rig.Input.Brake = n % 100;
            rig.Input.BaseSlip[n % 4] = n % 70;
            rig.Step();
        }

        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;

        // Allocation accounting is per allocation context (~8 KB); one allocation per frame would be >= 480 KB.
        Assert.True(allocated < 64 * 1024, "allocated " + allocated.ToString(CultureInfo.InvariantCulture) + " bytes");
    }

    [Test]
    public static void ProxyLoad_NeutralAndTransferDirections()
    {
        var loads = new double[Wheels.Count];
        ProxyLoad.Calc(0, 0, 1, 1, 5, 5, loads);
        for (int i = 0; i < Wheels.Count; i++)
        {
            Assert.Equal(ProxyLoad.Neutral, loads[i], "neutral");
        }

        // Positive sway loads the right wheels.
        ProxyLoad.Calc(2.5, 0, 1, 1, 5, 5, loads);
        Assert.Equal(37.5, loads[Wheels.FrontRight], "right loaded");
        Assert.Equal(12.5, loads[Wheels.FrontLeft], "left unloaded");

        // Positive surge (braking) loads the front wheels.
        ProxyLoad.Calc(0, 2.5, 1, 1, 5, 5, loads);
        Assert.Equal(37.5, loads[Wheels.FrontLeft], "front loaded");
        Assert.Equal(12.5, loads[Wheels.RearRight], "rear unloaded");

        // Full transfer is capped to 0..50, beyond-maximum accelerations are capped at 100 %.
        ProxyLoad.Calc(-50, 50, 1, 1, 5, 5, loads);
        Assert.Equal(ProxyLoad.Maximum, loads[Wheels.FrontLeft], "capped at maximum");
        Assert.Equal(0.0, loads[Wheels.RearRight], "capped at zero");

        // A zero maximum does not divide by zero.
        ProxyLoad.Calc(1, 1, 0.5, 0.5, 0, 0, loads);
        AssertFinite(loads, "zero max");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Equivalence sequences for the game presets in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    private static void RunGamePresets(int from, int to)
    {
        for (int p = from; p < to; p++)
        {
            GamePreset preset = GamePresets.Get(GamePresetNames[p], out string presetName);
            Assert.Equal(GamePresetNames[p], presetName, "preset lookup");
            RunEquivalenceSequence(preset, presetName, seed: 1000 + p);
        }
    }

    /// <summary>
    /// Feeds one randomized sequence to both pipelines and requires every exported value (and the debug values v1
    /// displayed) to be bit-identical.
    /// </summary>
    private static void RunEquivalenceSequence(GamePreset preset, string presetName, int seed)
    {
        var random = new Random(seed);
        HapticsSettings settings = RandomSettings(random);

        var legacy = new LegacyPipeline(settings, preset);
        var processor = new SlipLockProcessor();
        var maxG = new MaxGTracker();
        var tuning = new SlipLockTuning();
        settings.CopyTo(tuning);
        tuning.SlipSensitivity = CarProfile.DefaultSensitivity / 100.0;
        tuning.LockSensitivity = CarProfile.DefaultSensitivity / 100.0;

        var input = new SlipLockInputs { BaseSlipIsSigned = false, HasShakeItLock = false };
        var output = new SlipLockOutputs();
        var legacyRaw = new double[Wheels.Count];
        double wallTime = random.Next(0, 2) == 0 ? 0.0 : 1234.5;
        input.WallTime = wallTime;

        for (int frame = 0; frame < FramesPerSequence; frame++)
        {
            if (frame > 0)
            {
                wallTime += RandomDt(random);
                input.WallTime = wallTime;
            }

            RandomizeFrame(random, input);

            // Car change now and then (v1 reset its maxima, the shell resets the tracker).
            if (random.NextDouble() < 0.0005)
            {
                legacy.ResetMax();
                maxG.Reset();
            }

            Array.Copy(input.BaseSlip, legacyRaw, Wheels.Count);
            legacy.Frame(input, legacyRaw);

            maxG.Update(input.Sway, input.Surge);
            processor.Process(input, preset, tuning, maxG, output);

            CompareFrame(legacy, output, maxG, presetName, frame);
        }
    }

    private static void CompareFrame(LegacyPipeline legacy, SlipLockOutputs output, MaxGTracker maxG, string preset, int frame)
    {
        Dictionary<string, double> v1 = legacy.Exports;
        for (int i = 0; i < Wheels.Count; i++)
        {
            Same(v1[SlipKeys[i]], output.Slip[i], SlipKeys[i], preset, frame);
            Same(v1[LockKeys[i]], output.Lock[i], LockKeys[i], preset, frame);
            Same(v1[ABSKeys[i]], output.Abs[i], ABSKeys[i], preset, frame);
            Same(v1[TCKeys[i]], output.Tc[i], TCKeys[i], preset, frame);
            Same(v1[SlipBlendKeys[i]], output.SlipBlend[i], SlipBlendKeys[i], preset, frame);
            Same(v1[LockBlendKeys[i]], output.LockBlend[i], LockBlendKeys[i], preset, frame);
            Same(v1[SlipTCKeys[i]], output.SlipTc[i], SlipTCKeys[i], preset, frame);
            Same(v1[LockABSKeys[i]], output.LockAbs[i], LockABSKeys[i], preset, frame);
        }

        Same(v1["SlipLock.Slip.Mono"], output.SlipMono, "Slip.Mono", preset, frame);
        Same(v1["SlipLock.Lock.Mono"], output.LockMono, "Lock.Mono", preset, frame);
        Same(v1["SlipLock.ABS.Mono"], output.AbsMono, "ABS.Mono", preset, frame);
        Same(v1["SlipLock.TC.Mono"], output.TcMono, "TC.Mono", preset, frame);
        Same(v1["SlipLock.SlipBlend.Mono"], output.SlipBlendMono, "SlipBlend.Mono", preset, frame);
        Same(v1["SlipLock.LockBlend.Mono"], output.LockBlendMono, "LockBlend.Mono", preset, frame);
        Same(v1["SlipLock.SlipTC.Mono"], output.SlipTcMono, "SlipTC.Mono", preset, frame);
        Same(v1["SlipLock.LockABS.Mono"], output.LockAbsMono, "LockABS.Mono", preset, frame);
        Same(v1["SlipLock.MaxSway"], maxG.MaxSway, "MaxSway", preset, frame);
        Same(v1["SlipLock.MaxSurge"], maxG.MaxSurge, "MaxSurge", preset, frame);
        Same(v1["SlipLock.MaxDecel"], maxG.MaxDecel, "MaxDecel", preset, frame);

        // Debug values v1 displayed.
        Same(legacy.BaseSlipMono, output.BaseSlipMono, "BaseSlipMono", preset, frame);
        Same(legacy.BaseLockMono, output.BaseLockMono, "BaseLockMono", preset, frame);
        Same(legacy.BaseABSMono, output.BaseAbsMono, "BaseABSMono", preset, frame);
        Same(legacy.BaseTCMono, output.BaseTcMono, "BaseTCMono", preset, frame);
        Same(legacy.BaseSlipFL, output.BaseSlip[Wheels.FrontLeft], "BaseSlipFL", preset, frame);
        Same(legacy.BaseSlipFR, output.BaseSlip[Wheels.FrontRight], "BaseSlipFR", preset, frame);
        Same(legacy.BaseSlipRL, output.BaseSlip[Wheels.RearLeft], "BaseSlipRL", preset, frame);
        Same(legacy.BaseSlipRR, output.BaseSlip[Wheels.RearRight], "BaseSlipRR", preset, frame);
        Same(legacy.LoadFL, output.Loads[Wheels.FrontLeft], "LoadFL", preset, frame);
        Same(legacy.LoadFR, output.Loads[Wheels.FrontRight], "LoadFR", preset, frame);
        Same(legacy.LoadRL, output.Loads[Wheels.RearLeft], "LoadRL", preset, frame);
        Same(legacy.LoadRR, output.Loads[Wheels.RearRight], "LoadRR", preset, frame);
        if ((legacy.SlipTCMode == "TC") != output.AggregateUsesTc || (legacy.LockABSMode == "ABS") != output.AggregateUsesAbs)
        {
            Assert.Fail("aggregate mode differs (" + preset + ", frame " + frame.ToString(CultureInfo.InvariantCulture) + ")");
        }
    }

    private static string[] PropertyNames(string channel)
    {
        var names = new string[Wheels.Count];
        for (int i = 0; i < Wheels.Count; i++)
        {
            names[i] = "SlipLock." + channel + "." + Wheels.Names[i];
        }

        return names;
    }

    private static void Same(double expected, double actual, string name, string preset, int frame)
    {
        if (!(expected == actual))
        {
            Assert.Fail(string.Format(
                CultureInfo.InvariantCulture,
                "{0} differs from v1 ({1}, frame {2}): v1 {3:R}, v2 {4:R}",
                name,
                preset,
                frame,
                expected,
                actual));
        }
    }

    /// <summary>Post-processor settings, randomized once per sequence (v1 slider ranges, plus edge values).</summary>
    private static HapticsSettings RandomSettings(Random random)
    {
        var s = new HapticsSettings
        {
            SlipThrottleBlend = Pick(random, 0.2, 20.0, random.NextDouble() * 100),
            TCThrottleBlend = Pick(random, 0.2, 50.0, random.NextDouble() * 100),
            LockBrakeBlend = Pick(random, 0.2, 20.0, random.NextDouble() * 100),
            ABSBrakeBlend = Pick(random, 0.2, 50.0, random.NextDouble() * 100),
            SlipThreshold = Pick(random, 0.2, 5.0, random.NextDouble() * 50),
            LockThreshold = Pick(random, 0.2, 0.0, random.NextDouble() * 50),
            TCThreshold = random.NextDouble() * 50,
            ABSThreshold = random.NextDouble() * 50,
            GateSlipOnThrottle = random.Next(2) == 0,
            GateLockOnBrake = random.Next(2) == 0,
            SlipAttackMs = Pick(random, 0.2, 0.0, random.NextDouble() * 50),
            SlipReleaseMs = Pick(random, 0.1, 0.0, random.NextDouble() * 500),
            LockAttackMs = Pick(random, 0.2, 0.5, random.NextDouble() * 50),
            LockReleaseMs = random.NextDouble() * 500,
            ABSAttackMs = random.NextDouble() * 50,
            ABSReleaseMs = Pick(random, 0.2, 50.0, random.NextDouble() * 500),
            TCAttackMs = random.NextDouble() * 50,
            TCReleaseMs = random.NextDouble() * 500,
        };
        return s;
    }

    /// <summary>One randomized telemetry frame: pedals with exact edge values, ±30 accelerations, 0..300 km/h, flags.</summary>
    private static void RandomizeFrame(Random random, SlipLockInputs input)
    {
        input.Throttle = RandomPedal(random);
        input.Brake = RandomPedal(random);
        input.Sway = Pick(random, 0.1, 0.0, (random.NextDouble() * 60) - 30);
        input.Surge = Pick(random, 0.05, 0.1, Pick(random, 0.1, 0.0, (random.NextDouble() * 60) - 30));
        input.SpeedKmh = Pick(random, 0.05, 0.0, Pick(random, 0.2, random.NextDouble() * 80, random.NextDouble() * 300));

        // Flags persist for a while like real ABS/TC activity.
        if (random.NextDouble() < 0.1)
        {
            input.AbsActive = !input.AbsActive;
        }

        if (random.NextDouble() < 0.1)
        {
            input.TcActive = !input.TcActive;
        }

        if (random.NextDouble() < 0.01)
        {
            input.GameExportsTc = random.Next(2) == 0;
            input.GameExportsAbs = random.Next(2) == 0;
            input.TcLevel = RandomLevel(random);
            input.AbsLevel = RandomLevel(random);
        }

        double roll = random.NextDouble();
        if (roll < 0.15)
        {
            Array.Clear(input.BaseSlip, 0, Wheels.Count);
        }
        else if (roll < 0.45)
        {
            // Mono source: identical value on every wheel (iRacing/LMU ShakeIT).
            double value = RandomSlip(random);
            for (int i = 0; i < Wheels.Count; i++)
            {
                input.BaseSlip[i] = value;
            }
        }
        else
        {
            for (int i = 0; i < Wheels.Count; i++)
            {
                input.BaseSlip[i] = RandomSlip(random);
            }
        }
    }

    private static double RandomPedal(Random random)
    {
        double roll = random.NextDouble();
        if (roll < 0.25)
        {
            return 0;
        }

        if (roll < 0.33)
        {
            return 100;
        }

        if (roll < 0.38)
        {
            return 5; // synthesized-lock brake threshold
        }

        return random.NextDouble() * 100;
    }

    private static double RandomSlip(Random random) => Pick(random, 0.1, 0.0, (random.NextDouble() * 180) - 30);

    private static double RandomLevel(Random random)
    {
        switch (random.Next(3))
        {
            case 0:
                return -1;
            case 1:
                return 0;
            default:
                return 3;
        }
    }

    /// <summary>Frame times: mostly ~60 Hz with jitter, plus duplicates, bursts, stalls and a clock step backwards.</summary>
    private static double RandomDt(Random random)
    {
        double roll = random.NextDouble();
        if (roll < 0.02)
        {
            return 0;
        }

        if (roll < 0.07)
        {
            return 0.0003;
        }

        if (roll < 0.12)
        {
            return 0.05 + (random.NextDouble() * 0.45);
        }

        if (roll < 0.125)
        {
            return -0.01;
        }

        return FrameDt * (0.8 + (random.NextDouble() * 0.4));
    }

    private static double Pick(Random random, double probability, double special, double otherwise) =>
        random.NextDouble() < probability ? special : otherwise;

    /// <summary>Slip channel value for a single frame with neutral loads, no fade, no threshold and no smoothing.</summary>
    private static double RunSingleSlip(double rawSlip, double sensitivity)
    {
        var rig = new Rig(GamePresets.Default);
        rig.Tuning.SlipSensitivity = sensitivity;
        rig.SetAllBaseSlip(rawSlip);
        rig.Step();
        Assert.Equal(rig.Output.Slip[0], rig.Output.SlipBlend[0], "no post-processing in the rig");
        return rig.Output.Slip[0];
    }

    private static double[] LockLoads(Rig rig, GamePreset preset)
    {
        var loads = new double[Wheels.Count];
        ProxyLoad.Calc(rig.Input.Sway, rig.Input.Surge, preset.LockLat / 100, preset.LockLong / 100, rig.MaxG.MaxSway, rig.MaxG.MaxSurge, loads);
        return loads;
    }

    private static double Clamp100(double value) => Math.Max(0, Math.Min(100, value));

    private static void AssertAllFinite(SlipLockOutputs output)
    {
        AssertFinite(output.Slip, "Slip");
        AssertFinite(output.Lock, "Lock");
        AssertFinite(output.Abs, "Abs");
        AssertFinite(output.Tc, "Tc");
        AssertFinite(output.SlipBlend, "SlipBlend");
        AssertFinite(output.LockBlend, "LockBlend");
        AssertFinite(output.SlipTc, "SlipTc");
        AssertFinite(output.LockAbs, "LockAbs");
        AssertFinite(output.Loads, "Loads");
        AssertFinite(output.BaseSlip, "BaseSlip");
        AssertFinite(new[] { output.SlipMono, output.LockMono, output.SlipBlendMono, output.LockBlendMono, output.SlipTcMono, output.LockAbsMono }, "monos");
    }

    private static void AssertFinite(double[] values, string name)
    {
        for (int i = 0; i < values.Length; i++)
        {
            Assert.True(MathUtil.IsFinite(values[i]), name + "[" + i.ToString(CultureInfo.InvariantCulture) + "] not finite");
        }
    }

    /// <summary>
    /// Processor plus its collaborators, configured so a single frame is easy to reason about: zero thresholds, no
    /// blends, instant envelopes, no gates. Tests change only what they exercise.
    /// </summary>
    private sealed class Rig
    {
        private readonly GamePreset preset;

        public Rig(GamePreset preset)
        {
            this.preset = preset;
            Tuning.SlipThreshold = 0;
            Tuning.LockThreshold = 0;
            Tuning.AbsThreshold = 0;
            Tuning.TcThreshold = 0;
            Tuning.SlipThrottleBlend = 0;
            Tuning.LockBrakeBlend = 0;
            Tuning.TcThrottleBlend = 0;
            Tuning.AbsBrakeBlend = 0;
            Tuning.SlipAttackMs = 0;
            Tuning.SlipReleaseMs = 0;
            Tuning.LockAttackMs = 0;
            Tuning.LockReleaseMs = 0;
            Tuning.AbsAttackMs = 0;
            Tuning.AbsReleaseMs = 0;
            Tuning.TcAttackMs = 0;
            Tuning.TcReleaseMs = 0;
            Input.SpeedKmh = 200;
            Input.WallTime = 10.0;
        }

        public SlipLockProcessor Processor { get; } = new SlipLockProcessor();

        public SlipLockInputs Input { get; } = new SlipLockInputs();

        public SlipLockTuning Tuning { get; } = new SlipLockTuning();

        public SlipLockOutputs Output { get; } = new SlipLockOutputs();

        public MaxGTracker MaxG { get; } = new MaxGTracker();

        public void SetBaseSlip(double fl, double fr, double rl, double rr)
        {
            Input.BaseSlip[Wheels.FrontLeft] = fl;
            Input.BaseSlip[Wheels.FrontRight] = fr;
            Input.BaseSlip[Wheels.RearLeft] = rl;
            Input.BaseSlip[Wheels.RearRight] = rr;
        }

        public void SetAllBaseSlip(double value) => SetBaseSlip(value, value, value, value);

        public void SetShakeItLock(double fl, double fr, double rl, double rr)
        {
            Input.ShakeItLock[Wheels.FrontLeft] = fl;
            Input.ShakeItLock[Wheels.FrontRight] = fr;
            Input.ShakeItLock[Wheels.RearLeft] = rl;
            Input.ShakeItLock[Wheels.RearRight] = rr;
        }

        /// <summary>Advances the clock one 60 Hz frame and processes.</summary>
        public void Step()
        {
            Input.WallTime += FrameDt;
            Process();
        }

        /// <summary>Processes at the current clock value (shell order: max G first).</summary>
        public void Process()
        {
            MaxG.Update(Input.Sway, Input.Surge);
            Processor.Process(Input, preset, Tuning, MaxG, Output);
        }
    }
}
