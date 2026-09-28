using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using User.SlipLockPropertiesCalc.Balance;
using User.SlipLockPropertiesCalc.Core;

namespace User.SlipLockPropertiesCalc.Tests;

/// <summary>
/// Synthetic-data tests for <see cref="BalanceLearner"/>, its numeric building blocks and <see cref="ParamResolver"/>.
/// Model samples follow y = v·θeff/r = (1/G)(1 + K·v²) with known G/K plus noise and gross outliers, generated inside
/// the estimator's linear-region learning gates; turn-in events follow a first-order lag with a known τ.
/// </summary>
internal sealed class BalanceLearnerTests
{
    private const double Dt = 1.0 / 60.0;

    /// <summary>
    /// Json.NET's "R" double formatting is not always exact on .NET Framework x64 (~1 ulp for a few values in a
    /// million), so "identical" after a JSON round trip means equal to within this relative tolerance.
    /// </summary>
    private const double RoundTripTolerance = 1e-12;

    // ------------------------------------------------------------------ G / K fit

    [Test]
    public void ModelFit_ConvergesForKnownGainAndUndersteer()
    {
        double[] gains = { 0.02, 0.03, 0.05 };
        double[] factors = { 0.0, 0.0013, 0.003 };
        int seed = 100;
        foreach (double g in gains)
        {
            foreach (double k in factors)
            {
                var tuning = new BalanceTuning();
                var learner = new BalanceLearner(tuning, NullLog.Instance);
                new ModelSampler(seed++, g, k).Feed(learner, 5000);

                string label = string.Format(CultureInfo.InvariantCulture, "G={0} K={1}", g, k);
                Assert.True(learner.VSpread >= 15.0, label + ": speed spread " + learner.VSpread);
                Assert.Near(g, learner.G.Value, 0.03 * g, label + ": G");
                Assert.Near(k, learner.K.Value, Math.Max(0.2 * k, 0.2 * tuning.KDefault), label + ": K");
                Assert.True(learner.G.Confidence >= 0.7, label + ": G confidence " + learner.G.Confidence);
                Assert.True(learner.K.Confidence >= 0.7, label + ": K confidence " + learner.K.Confidence);
                Assert.True(learner.RejectedModelSamples > 0.03 * 5000, label + ": outliers rejected");
            }
        }
    }

    [Test]
    public void ModelFit_SmallSpeedSpread_LeavesKUnlearnedAndFitsGWithThePrior()
    {
        const double TrueG = 0.03;
        const double TrueK = 0.0013;
        const double MeanSpeed = 31.0;

        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        learner.SetKPrior(TrueK);
        var sampler = new ModelSampler(7, TrueG, TrueK) { MinSpeed = 28.0, MaxSpeed = 34.0 };
        sampler.Feed(learner, 5000);

        Assert.True(learner.VSpread < 15.0, "spread " + learner.VSpread);
        Assert.True(double.IsNaN(learner.K.Value), "K not learned");
        Assert.Equal(0.0, learner.K.Confidence, "K confidence");
        Assert.Near(TrueG, learner.G.Value, 0.03 * TrueG, "G with the right prior");
        Assert.True(learner.G.Confidence >= 0.7, "G confidence " + learner.G.Confidence);

        // With a wrong prior the 1-parameter G compensates at the driven speeds: r_ss there stays right.
        const double WrongK = 0.0025;
        learner.SetKPrior(WrongK);
        double expected = TrueG / (1.0 + (TrueK * MeanSpeed * MeanSpeed));
        double actual = learner.G.Value / (1.0 + (WrongK * MeanSpeed * MeanSpeed));
        Assert.Near(expected, actual, 0.03 * expected, "gain at the driven speed");
        Assert.True(learner.G.Value > TrueG * 1.3, "G absorbed the prior error");
    }

    [Test]
    public void ModelFit_ImplausibleResults_AreClampedWithHalfConfidence()
    {
        BalanceLearner plausibleG = Fit(0.08, 0.0013);
        BalanceLearner tooHighG = Fit(0.15, 0.0013);
        Assert.Near(0.08, plausibleG.G.Value, 0.03 * 0.08, "reference G");
        Assert.Equal(0.1, tooHighG.G.Value, "G clamped to the plausible maximum");
        Assert.InRange(tooHighG.G.Confidence / plausibleG.G.Confidence, 0.4, 0.6, "G confidence halved");

        BalanceLearner plausibleK = Fit(0.026, 0.003);
        BalanceLearner tooHighK = Fit(0.026, 0.008);
        Assert.Near(0.003, plausibleK.K.Value, 0.2 * 0.003, "reference K");
        Assert.Equal(0.004, tooHighK.K.Value, "K clamped to the plausible maximum");
        Assert.InRange(tooHighK.K.Confidence / plausibleK.K.Confidence, 0.4, 0.6, "K confidence halved");
    }

    [Test]
    public void ModelFit_IgnoresInvalidSamples()
    {
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        int version = learner.Version;

        learner.AddModelSample(double.NaN, 0.1, 0.1, Dt);
        learner.AddModelSample(30, double.NaN, 0.1, Dt);
        learner.AddModelSample(30, 0.1, double.NaN, Dt);
        learner.AddModelSample(30, 0.1, 0.1, double.NaN);
        learner.AddModelSample(30, 0.1, 0.1, 0.0);
        learner.AddModelSample(30, 0.1, 0.1, -Dt);
        learner.AddModelSample(30, 0.1, double.PositiveInfinity, Dt);
        learner.AddModelSample(30, 0.1, 0.0, Dt);        // r = 0
        learner.AddModelSample(30, 0.1, 1e-6, Dt);       // r ~ 0 (y would explode)
        learner.AddModelSample(0.0, 0.1, 0.1, Dt);       // standing
        learner.AddModelSample(-20, 0.1, 0.1, Dt);       // reverse
        learner.AddModelSample(200, 0.1, 0.1, Dt);       // 720 km/h: glitch
        learner.AddModelSample(30, 0.1, -0.1, Dt);       // countersteer: signs disagree
        learner.AddModelSample(30, 0.0, 0.1, Dt);        // no steering

        Assert.Equal(version, learner.Version, "ignored samples change nothing");
        Assert.False(learner.Dirty, "not dirty");
        Assert.Equal(0L, learner.G.Samples, "no samples");

        // Garbage that passes the input checks (y far outside any real car) is rejected, not fitted.
        learner.AddModelSample(30, 1.0, 0.002, Dt);
        Assert.Equal(1L, learner.RejectedModelSamples, "implausible y rejected");
        Assert.Equal(0L, learner.G.Samples, "and not fitted");
    }

    [Test]
    public void ModelFit_SurvivesJunkAndStillConverges()
    {
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        var rng = new Random(3);
        for (int i = 0; i < 5000; i++)
        {
            // Alternating signs, huge and tiny magnitudes, occasional non-finite values.
            double v = (rng.NextDouble() * 200.0) - 20.0;
            double theta = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-4, 3));
            double r = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-5, 2));
            if (i % 97 == 0)
            {
                r = double.NaN;
            }

            learner.AddModelSample(v, theta, r, Dt);
            AssertFiniteOrNaN(learner.G, "G during junk");
            AssertFiniteOrNaN(learner.K, "K during junk");
        }

        // Plausible junk that got in during the warm-up stays in the fit's memory (λ = 0.9995) and keeps the fit
        // quality low until it is forgotten; the value itself recovers much earlier.
        var clean = new ModelSampler(4, 0.03, 0.0013);
        clean.Feed(learner, 5000);
        Assert.Near(0.03, learner.G.Value, 0.03 * 0.03, "G after junk");
        clean.Feed(learner, 15000);
        Assert.Near(0.03, learner.G.Value, 0.03 * 0.03, "G once the junk is forgotten");
        Assert.True(learner.G.Confidence >= 0.7, "G confidence once the junk is forgotten: " + learner.G.Confidence);
    }

    // ------------------------------------------------------------------ theta0

    [Test]
    public void Theta0_LearnsFromStraightSamplesWithCountBasedConfidence()
    {
        const double TrueOffset = 0.03;
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        var rng = new Random(5);

        FeedStraight(learner, rng, TrueOffset, 29);
        Assert.True(double.IsNaN(learner.Theta0.Value), "not published before 30 samples");

        FeedStraight(learner, rng, TrueOffset, 150 - 29);
        Assert.Near(0.5, learner.Theta0.Confidence, 1e-12, "confidence at 150 samples");

        FeedStraight(learner, rng, TrueOffset, 150);
        Assert.Equal(300L, learner.Theta0.Samples, "samples");
        Assert.Near(1.0, learner.Theta0.Confidence, 1e-12, "confidence at 300 samples");
        Assert.Near(TrueOffset, learner.Theta0.Value, 0.002, "offset");

        // Beyond the plausible ±10°: clamped, confidence halved.
        var bent = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        FeedStraight(bent, rng, 17.0 * MathUtil.DegToRad, 300);
        Assert.Near(10.0 * MathUtil.DegToRad, bent.Theta0.Value, 1e-12, "clamped");
        Assert.Near(0.5, bent.Theta0.Confidence, 1e-12, "halved");

        // dt <= 0 and glitches are ignored.
        int version = learner.Version;
        learner.AddStraightSample(0.01, 0.0);
        learner.AddStraightSample(0.01, double.NaN);
        learner.AddStraightSample(double.NaN, Dt);
        learner.AddStraightSample(3.0, Dt);
        Assert.Equal(version, learner.Version, "invalid straight samples ignored");
    }

    // ------------------------------------------------------------------ ay_max

    [Test]
    public void AyMax_Is98thPercentileWithCountBasedConfidence()
    {
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        var rng = new Random(8);
        for (int i = 0; i < 1499; i++)
        {
            learner.AddLateralSample(20.0 * rng.NextDouble());
        }

        Assert.True(double.IsNaN(learner.AyMax.Value), "not learned below 1500 samples");
        Assert.Equal(0.0, learner.AyMax.Confidence, "no confidence yet");

        learner.AddLateralSample(20.0 * rng.NextDouble());
        Assert.True(MathUtil.IsFinite(learner.AyMax.Value), "learned at 1500 samples");
        Assert.Near(0.5, learner.AyMax.Confidence, 1e-12, "confidence at 1500");

        for (int i = 0; i < 1500; i++)
        {
            learner.AddLateralSample(-20.0 * rng.NextDouble()); // sign is irrelevant
        }

        Assert.Near(1.0, learner.AyMax.Confidence, 1e-12, "confidence at 3000");
        Assert.Near(19.6, learner.AyMax.Value, 0.35, "p98 of uniform [0, 20]");
        Assert.Equal(3000L, learner.AyMax.Samples, "samples");

        learner.AddLateralSample(500.0); // impact spike, not cornering
        learner.AddLateralSample(double.NaN);
        Assert.Equal(3000L, learner.AyMax.Samples, "implausible samples ignored");
    }

    [Test]
    public void AyMax_HistogramHalvesForSlowForgetting()
    {
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        var rng = new Random(9);
        for (int i = 0; i < 250000; i++)
        {
            learner.AddLateralSample(10.0 * rng.NextDouble());
        }

        var state = new BalanceLearnedState();
        learner.SaveTo(state);
        long mass = 0;
        foreach (int count in state.AyHistogram)
        {
            mass += count;
        }

        Assert.InRange(mass, 100000, 200000, "histogram mass after halving");
        Assert.Equal(250000L, learner.AyMax.Samples, "lifetime samples kept");
        Assert.Near(9.8, learner.AyMax.Value, 0.3, "percentile unchanged by halving");
    }

    // ------------------------------------------------------------------ beta envelope

    [Test]
    public void BetaEnvelope_PerBinPercentileWithInterpolationAndFallbacks()
    {
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        Assert.Equal(0.0, learner.BetaEnvelopeDeg(0.5), "0 before anything is learned");

        var rng = new Random(10);
        for (int bin = 0; bin < BetaEnvelope.LoadBinCount; bin++)
        {
            int samples = bin == 5 ? 150 : 400; // bin 5 stays unlearned
            double betaMax = 1.0 + (0.5 * bin);
            for (int i = 0; i < samples; i++)
            {
                double ayNorm = (bin + rng.NextDouble()) / BetaEnvelope.LoadBinCount;
                learner.AddBodySlipSample(ayNorm, betaMax * rng.NextDouble());
            }
        }

        Assert.Equal(9, learner.BetaEnvelopeLearnedBins, "learned bins");
        for (int bin = 0; bin < BetaEnvelope.LoadBinCount; bin++)
        {
            if (bin == 5)
            {
                continue;
            }

            double expected = 0.95 * (1.0 + (0.5 * bin));
            Assert.Near(expected, learner.BetaEnvelopeDeg(Centre(bin)), 0.1 + (0.05 * expected), "bin " + bin);
        }

        // Linear between centres; below the first / above the last centre: flat.
        double between = learner.BetaEnvelopeDeg(0.3);
        Assert.Near(0.5 * (learner.BetaEnvelopeDeg(Centre(2)) + learner.BetaEnvelopeDeg(Centre(3))), between, 1e-12, "interpolated");
        Assert.Equal(learner.BetaEnvelopeDeg(Centre(0)), learner.BetaEnvelopeDeg(0.0), "flat below");
        Assert.Equal(learner.BetaEnvelopeDeg(Centre(9)), learner.BetaEnvelopeDeg(1.0), "flat above");
        Assert.Equal(learner.BetaEnvelopeDeg(Centre(9)), learner.BetaEnvelopeDeg(3.0), "clamped above 1");

        // Unlearned bin 5 borrows the nearest learned bin below it.
        Assert.Equal(learner.BetaEnvelopeDeg(Centre(4)), learner.BetaEnvelopeDeg(Centre(5)), "unlearned bin");

        // Bins below the first learned bin read 0.
        var sparse = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        for (int i = 0; i < 199; i++)
        {
            sparse.AddBodySlipSample(Centre(3), 2.0);
        }

        Assert.Equal(0.0, sparse.BetaEnvelopeDeg(Centre(3)), "199 samples are not enough");
        sparse.AddBodySlipSample(Centre(3), 2.0);
        Assert.Near(2.0, sparse.BetaEnvelopeDeg(Centre(3)), 0.1, "learned at 200");
        Assert.Equal(0.0, sparse.BetaEnvelopeDeg(Centre(1)), "no learned bin below");
        Assert.Near(2.0, sparse.BetaEnvelopeDeg(Centre(8)), 0.1, "carried upward");
    }

    // ------------------------------------------------------------------ alpha peak

    [Test]
    public void AlphaPeak_IsUnitAgnostic()
    {
        foreach (double peak in new[] { 0.1, 6.0 }) // radians and degrees
        {
            var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
            var rng = new Random(11);
            for (int i = 0; i < 299; i++)
            {
                double alpha = peak * (0.5 + (0.5 * rng.NextDouble()));
                learner.AddSlipAngleSample(alpha, -0.8 * alpha);
            }

            Assert.True(double.IsNaN(learner.AlphaPeak.Value), "not learned below 300 samples");
            learner.AddSlipAngleSample(double.NaN, 0.1);
            learner.AddSlipAngleSample(0.0, 0.0);
            Assert.Equal(299L, learner.AlphaPeak.Samples, "invalid samples ignored");

            for (int i = 0; i < 301; i++)
            {
                double alpha = peak * (0.5 + (0.5 * rng.NextDouble()));
                learner.AddSlipAngleSample(0.7 * alpha, alpha);
            }

            Assert.Near(0.95 * peak, learner.AlphaPeak.Value, 0.05 * peak, "p90, peak " + peak);
            Assert.Near(1.0, learner.AlphaPeak.Confidence, 1e-12, "confidence at 600");
        }
    }

    // ------------------------------------------------------------------ tau

    [Test]
    public void TauYaw_RecoversLagFromTurnInEvents()
    {
        foreach (double tau in new[] { 0.08, 0.15, 0.25 })
        {
            var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
            var generator = new TurnInGenerator(20, tau);
            generator.FeedEvents(learner, 9);
            Assert.True(double.IsNaN(learner.TauYaw.Value), "not learned before 10 events, tau " + tau);

            generator.FeedEvents(learner, 3);
            Assert.True(learner.TauYaw.Samples >= 10, "events detected: " + learner.TauYaw.Samples);
            Assert.Near(tau, learner.TauYaw.Value, 0.02, "tau");
            Assert.Near(Math.Min(1.0, learner.TauYaw.Samples / 20.0), learner.TauYaw.Confidence, 1e-12, "confidence");
        }
    }

    [Test]
    public void TauYaw_RobustToNoiseAndGainError()
    {
        foreach (double tau in new[] { 0.08, 0.15, 0.25 })
        {
            var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);

            // Noisy yaw rate, and r_ss computed with a wrong (not yet learned) gain: the fitted scale absorbs it.
            var generator = new TurnInGenerator(21, tau) { YawNoise = 0.005, ModelGainError = 0.7 };
            generator.FeedEvents(learner, 25);
            Assert.True(learner.TauYaw.Samples >= 20, "events accepted: " + learner.TauYaw.Samples);
            Assert.Near(tau, learner.TauYaw.Value, 0.02, "tau with noise");
            Assert.Near(1.0, learner.TauYaw.Confidence, 1e-12, "full confidence");
        }
    }

    [Test]
    public void TauYaw_AbortedAndLockedEventsDoNotCount()
    {
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        var generator = new TurnInGenerator(22, 0.15) { AbortAfterTicks = 10 };
        generator.FeedEvents(learner, 5);
        Assert.Equal(0L, learner.TauYaw.Samples, "aborted events");

        learner.Locked = true;
        new TurnInGenerator(23, 0.15).FeedEvents(learner, 5);
        Assert.Equal(0L, learner.TauYaw.Samples, "locked");

        learner.Locked = false;
        new TurnInGenerator(24, 0.15).FeedEvents(learner, 5);
        Assert.Equal(5L, learner.TauYaw.Samples, "counted again when unlocked");
    }

    // ------------------------------------------------------------------ session layer / lock / reset

    [Test]
    public void Session_OverridesGAfterPersistentDamage_BaselineUntouched()
    {
        const double CleanG = 0.026;
        const double DamagedG = CleanG * 1.2;
        var tuning = new BalanceTuning();
        var learner = new BalanceLearner(tuning, NullLog.Instance);
        var clean = new ModelSampler(30, CleanG, 0.0013);
        clean.Feed(learner, 4000);
        double baselineG = learner.G.Value;
        Assert.True(learner.G.Confidence >= tuning.LearnedConfidenceThreshold, "baseline trusted");

        learner.ResetSession();
        Assert.Equal(baselineG, learner.SessionReferenceG, "reference taken at session start");
        for (int i = 0; i < 1500; i++)
        {
            clean.FeedOne(learner);
            Assert.False(learner.SessionGActive, "no override on clean data (sample " + i + ")");
        }

        var damaged = new ModelSampler(31, DamagedG, 0.0013);
        int firstDeviating = -1;
        int activated = -1;
        for (int i = 0; i < 4000 && activated < 0; i++)
        {
            damaged.FeedOne(learner);
            double deviation = Math.Abs(learner.SessionG - learner.SessionReferenceG) / learner.SessionReferenceG;
            if (firstDeviating < 0 && deviation > 0.15)
            {
                firstDeviating = i;
            }

            if (learner.SessionGActive)
            {
                activated = i;
            }
        }

        Assert.True(firstDeviating >= 0 && activated >= 0, "override activated");
        Assert.True(activated - firstDeviating >= 59, "needs 60 deviating samples, took " + (activated - firstDeviating + 1));
        Assert.True(activated < 1000, "activated within " + activated + " damaged samples");

        damaged.Feed(learner, 500);
        Assert.True(learner.SessionGActive, "override stays active");
        Assert.Near(DamagedG, learner.SessionG, 0.03 * DamagedG, "session G");

        // Until the override takes over (~360 samples at λ = 0.995 plus 60 deviating samples) the baseline accepts
        // damaged samples once its outlier gate has adapted (~3 % drift); from then on it is frozen.
        Assert.Near(baselineG, learner.G.Value, 0.05 * baselineG, "baseline G not dragged along");
        double frozenG = learner.G.Value;
        damaged.Feed(learner, 1000);
        Assert.Equal(frozenG, learner.G.Value, "baseline G frozen while the session overrides it");

        var resolver = new ParamResolver(tuning);
        var parameters = new EffectiveParams();
        resolver.Resolve(new BalanceOverrides(), learner, string.Empty, string.Empty, double.NaN, parameters);
        Assert.Equal(ParamSource.Session, parameters.GSource, "resolver uses the session G");
        Assert.Equal(learner.SessionG, parameters.G, "session value");

        // The persisted baseline never contains the session value.
        var state = new BalanceLearnedState();
        learner.SaveTo(state);
        var reloaded = new BalanceLearner(tuning, NullLog.Instance);
        reloaded.Load(state);
        Assert.Equal(frozenG, reloaded.G.Value, "persisted G is the baseline");
        Assert.False(reloaded.SessionGActive, "session not persisted");

        learner.ResetSession();
        Assert.False(learner.SessionGActive, "session reset");
        resolver.Resolve(new BalanceOverrides(), learner, string.Empty, string.Empty, double.NaN, parameters);
        Assert.Equal(ParamSource.Learned, parameters.GSource, "back to the baseline");
    }

    [Test]
    public void Session_ReferenceUsesTheKPriorSetAfterLoad()
    {
        // The estimator loads the learned state first and resolves the car's K prior afterwards. That must not count
        // as a change of the model inputs during the session (which disables the session G override).
        const double PrototypeK = 0.0008;
        var tuning = new BalanceTuning();
        var trained = new BalanceLearner(tuning, NullLog.Instance);
        trained.SetKPrior(PrototypeK);
        var clean = new ModelSampler(34, 0.030, PrototypeK) { MinSpeed = 20.0, MaxSpeed = 30.0 }; // spread < 15: K not learned
        clean.Feed(trained, 1500);
        Assert.True(trained.G.Confidence >= tuning.LearnedConfidenceThreshold, "trained G trusted");
        var state = new BalanceLearnedState();
        trained.SaveTo(state);

        var learner = new BalanceLearner(tuning, NullLog.Instance); // prior KDefault, like the previous car's
        learner.Load(state);
        learner.SetKPrior(PrototypeK);
        Assert.Near(trained.G.Value, learner.SessionReferenceG, 1e-9, "reference taken with the car's prior");

        var damaged = new ModelSampler(35, 0.020, PrototypeK) { MinSpeed = 20.0, MaxSpeed = 30.0 };
        damaged.Feed(learner, 600);
        Assert.True(MathUtil.IsFinite(learner.SessionReferenceG), "reference kept");
        Assert.True(learner.SessionGActive, "session G override after damage: " + learner.SessionG);

        // A real change of the prior during the session still invalidates the comparison.
        learner.SetKPrior(0.002);
        damaged.Feed(learner, 10);
        Assert.True(double.IsNaN(learner.SessionReferenceG), "reference dropped after a mid-session K change");
    }

    [Test]
    public void InvertSteeringOffset_NegatesTheLearnedOffset()
    {
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        FeedStraight(learner, new Random(36), 2.0 * MathUtil.DegToRad, 600);
        double before = learner.Theta0.Value;
        long samples = learner.Theta0.Samples;
        learner.SaveTo(new BalanceLearnedState());
        Assert.False(learner.Dirty, "saved");

        learner.InvertSteeringOffset();
        Assert.Near(-before, learner.Theta0.Value, 1e-12, "offset negated");
        Assert.Equal(samples, learner.Theta0.Samples, "sample count kept");
        Assert.True(learner.Dirty, "baseline changed");
        Assert.True(double.IsNaN(learner.SessionTheta0), "session layer restarted");
    }

    [Test]
    public void Session_OverridesTheta0AfterBentSteering()
    {
        var tuning = new BalanceTuning();
        var learner = new BalanceLearner(tuning, NullLog.Instance);
        var rng = new Random(32);
        FeedStraight(learner, rng, 0.0, 400);
        Assert.False(learner.SessionTheta0Active, "no override when straight");

        const double Bent = 4.0 * MathUtil.DegToRad;
        FeedStraight(learner, rng, Bent, 600);
        Assert.True(learner.SessionTheta0Active, "override after bent steering");
        Assert.Near(Bent, learner.SessionTheta0, 0.2 * MathUtil.DegToRad, "session offset");
        Assert.True(Math.Abs(learner.Theta0.Value) < 1.5 * MathUtil.DegToRad, "baseline kept: " + (learner.Theta0.Value * MathUtil.RadToDeg));

        learner.ResetSession();
        Assert.False(learner.SessionTheta0Active, "reset");
        Assert.True(double.IsNaN(learner.SessionTheta0), "no session offset after reset");
    }

    [Test]
    public void Locked_FreezesBaselineWhileSessionKeepsRunning()
    {
        var tuning = new BalanceTuning();
        var learner = new BalanceLearner(tuning, NullLog.Instance);
        FeedMixed(learner, 40, 0.026, 0.0013, 0.01, 0.15, rounds: 12);
        learner.ResetSession(); // next session on top of the learned baseline
        var before = new BalanceLearnedState();
        learner.SaveTo(before);
        string frozen = JsonConvert.SerializeObject(before);
        Assert.False(learner.Dirty, "clean after save");

        learner.Locked = true;
        FeedMixed(learner, 41, 0.026 * 1.3, 0.0013, 0.01, 0.25, rounds: 8); // changed gain, other lag
        Assert.True(learner.SessionGActive, "session G adapted to the changed car");
        FeedStraight(learner, new Random(43), 0.01 + (3.0 * MathUtil.DegToRad), 600); // bent steering
        Assert.True(learner.SessionTheta0Active, "session offset adapted");
        Assert.False(learner.Dirty, "locked learner stays clean");

        var after = new BalanceLearnedState();
        learner.SaveTo(after);
        Assert.Equal(frozen, JsonConvert.SerializeObject(after), "baseline unchanged while locked");
        Assert.True(learner.SessionModelSamples > 1000, "session layer kept learning");

        learner.Locked = false;
        FeedMixed(learner, 42, 0.026, 0.0013, 0.01, 0.15, rounds: 1);
        Assert.True(learner.Dirty, "learning again when unlocked");
    }

    [Test]
    public void Session_NoGOverrideWhileANewCarIsStillBeingLearned()
    {
        // First session of a new car whose steering offset (1.5°) is learned part-way: until then the estimator
        // subtracts θ0 = 0, so the early G samples are biased. That is learning, not a changed car.
        const double TrueG = 0.03;
        const double TrueOffset = 1.5 * MathUtil.DegToRad;
        var tuning = new BalanceTuning();
        var learner = new BalanceLearner(tuning, NullLog.Instance);
        var sampler = new ModelSampler(34, TrueG, 0.0017)
        {
            SteeringOffset = TrueOffset,
            OffsetInUse = () => learner.Theta0.Confidence >= tuning.LearnedConfidenceThreshold ? learner.Theta0.Value : 0.0,
        };
        var rng = new Random(35);
        for (int round = 0; round < 24; round++)
        {
            for (int i = 0; i < 250; i++)
            {
                sampler.FeedOne(learner);
                Assert.False(learner.SessionGActive, "no G override during the first session");
            }

            FeedStraight(learner, rng, TrueOffset, 30);
        }

        Assert.True(double.IsNaN(learner.SessionReferenceG), "no G reference without a baseline at session start");
        Assert.Near(TrueOffset, learner.Theta0.Value, 0.2 * MathUtil.DegToRad, "offset learned");
        Assert.Near(TrueG, learner.G.Value, 0.05 * TrueG, "gain learned");

        // Next session: the reference exists. A mid-session change of the model inputs (here a manual K) drops it,
        // so even a real gain change is not reported against a reference fitted with other inputs.
        learner.ResetSession();
        Assert.Near(learner.G.Value, learner.SessionReferenceG, 0.0, "reference from the established baseline");
        learner.SetKPrior(0.0008, isManual: true);
        var damaged = new ModelSampler(36, TrueG * 1.3, 0.0017) { SteeringOffset = TrueOffset, OffsetInUse = sampler.OffsetInUse };
        for (int i = 0; i < 2000; i++)
        {
            damaged.FeedOne(learner);
            Assert.False(learner.SessionGActive, "no comparison after the inputs changed (sample " + i + ")");
        }

        Assert.True(double.IsNaN(learner.SessionReferenceG), "reference dropped");
    }

    [Test]
    public void ResetBaseline_ForgetsEverything()
    {
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        FeedMixed(learner, 50, 0.03, 0.002, 0.02, 0.15, rounds: 12);
        learner.SaveTo(new BalanceLearnedState());
        Assert.True(MathUtil.IsFinite(learner.G.Value) && MathUtil.IsFinite(learner.TauYaw.Value), "trained");
        int version = learner.Version;

        learner.ResetBaseline();

        Assert.True(learner.Dirty, "dirty after reset");
        Assert.True(learner.Version != version, "version changed");
        foreach (LearnedParam param in new[] { learner.G, learner.K, learner.Theta0, learner.TauYaw, learner.AyMax, learner.AlphaPeak })
        {
            Assert.True(double.IsNaN(param.Value), "value cleared");
            Assert.Equal(0.0, param.Confidence, "confidence cleared");
            Assert.Equal(0L, param.Samples, "samples cleared");
        }

        Assert.Equal(0.0, learner.BetaEnvelopeDeg(0.5), "envelope cleared");
        Assert.Equal(0.0, learner.VSpread, "spread cleared");
        Assert.False(learner.SessionGActive || learner.SessionTheta0Active, "session cleared");
        Assert.Equal(0L, learner.SessionModelSamples, "session samples cleared");
    }

    [Test]
    public void Version_ChangesOnlyWhenStateChanges()
    {
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        int version = learner.Version;

        learner.AddLateralSample(double.NaN);
        learner.AddBodySlipSample(double.NaN, 1.0);
        learner.AddSlipAngleSample(double.NaN, double.NaN);
        learner.AddYawTrace(0, Dt, 0, 0, 0, 30);
        learner.SetKPrior(learner.KPrior);
        learner.SetKPrior(double.NaN);
        Assert.Equal(version, learner.Version, "no-ops keep the version");

        learner.AddLateralSample(5.0);
        Assert.True(learner.Version != version, "sample");
        version = learner.Version;

        learner.SetKPrior(0.002);
        Assert.True(learner.Version != version, "new K prior");
        Assert.Equal(0.002, learner.KPrior, "K prior stored");
        version = learner.Version;

        learner.SetKPrior(0.002, isManual: true);
        Assert.True(learner.Version != version && learner.KPriorIsManual, "manual flag");
        version = learner.Version;

        learner.ResetSession();
        Assert.True(learner.Version != version, "session reset");
        version = learner.Version;

        learner.Load(null);
        Assert.True(learner.Version != version, "load");
    }

    // ------------------------------------------------------------------ persistence

    [Test]
    public void Persistence_JsonRoundTripResumesIdentically()
    {
        var tuning = new BalanceTuning();
        var original = new BalanceLearner(tuning, NullLog.Instance);
        FeedMixed(original, 60, 0.03, 0.002, 0.02, 0.15, rounds: 12);
        Assert.False(original.YawEventInProgress, "saved between events");
        Assert.True(original.Dirty, "dirty before save");
        Assert.True(MathUtil.IsFinite(original.K.Value) && MathUtil.IsFinite(original.TauYaw.Value)
            && MathUtil.IsFinite(original.AlphaPeak.Value) && MathUtil.IsFinite(original.AyMax.Value)
            && original.BetaEnvelopeLearnedBins == BetaEnvelope.LoadBinCount, "every part learned");

        var saved = new BalanceLearnedState();
        original.SaveTo(saved);
        Assert.False(original.Dirty, "clean after save");
        string json = JsonConvert.SerializeObject(saved, Formatting.Indented);
        BalanceLearnedState parsed = JsonConvert.DeserializeObject<BalanceLearnedState>(json);

        var reloaded = new BalanceLearner(tuning, NullLog.Instance);
        reloaded.Load(parsed);
        Assert.False(reloaded.Dirty, "clean after load");
        AssertSameBaseline(original, reloaded, "after load");

        // Readable summary is written too.
        Assert.Near(original.G.Value, saved.G.Value, 0.0, "summary G");
        Assert.Near(original.Theta0.Value * MathUtil.RadToDeg, saved.Theta0Deg.Value, 1e-12, "summary theta0 in degrees");

        FeedMixed(original, 61, 0.03, 0.002, 0.02, 0.15, rounds: 4);
        FeedMixed(reloaded, 61, 0.03, 0.002, 0.02, 0.15, rounds: 4);
        AssertSameBaseline(original, reloaded, "after continued learning");
    }

    [Test]
    public void Persistence_ToleratesNullShortInvalidAndNewerData()
    {
        var log = new RecordingLog();
        var learner = new BalanceLearner(new BalanceTuning(), log);

        learner.Load(null);
        learner.Load(new BalanceLearnedState());
        learner.Load(JsonConvert.DeserializeObject<BalanceLearnedState>("{}"));
        learner.Load(JsonConvert.DeserializeObject<BalanceLearnedState>("{ \"SchemaVersion\": 1, \"AyHistogram\": null, \"BetaHistograms\": [ null ] }"));
        Assert.Equal(0, log.Warnings.Count, "empty data is valid");
        Assert.True(double.IsNaN(learner.G.Value), "empty");

        var broken = new BalanceLearnedState
        {
            FitSamples = 500,
            FitSumW = double.NaN,
            FitSumWY = 10,
            Theta0Ema = double.PositiveInfinity,
            Theta0Samples = 100,
            AyHistogram = new[] { 5, 3 },
            BetaHistograms = new[] { null, new[] { 1, -2 }, new[] { 4 } },
            AlphaHistogram = new int[0],
            TauEvents = new[] { 0.12, double.NaN, 3.0, 0.2 },
            TauEventsTotal = -5,
        };
        learner.Load(broken);
        Assert.Equal(1, log.Warnings.Count, "invalid parts reported once");
        Assert.True(double.IsNaN(learner.G.Value) && learner.G.Samples == 0, "invalid fit reset");
        Assert.True(double.IsNaN(learner.Theta0.Value) && learner.Theta0.Samples == 0, "invalid theta0 reset");
        Assert.Equal(8L, learner.AyMax.Samples, "short histogram loaded");
        Assert.Equal(2L, learner.TauYaw.Samples, "valid events kept");

        // A trained state with truncated arrays still loads.
        var trained = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        FeedMixed(trained, 70, 0.03, 0.002, 0.02, 0.15, rounds: 12);
        var state = new BalanceLearnedState();
        trained.SaveTo(state);
        Array.Resize(ref state.AyHistogram, 10);
        Array.Resize(ref state.BetaHistograms, 3);
        state.AlphaHistogram = null;
        learner.Load(state);
        Assert.Near(trained.G.Value, learner.G.Value, RoundTripTolerance * trained.G.Value, "fit intact");
        Assert.True(double.IsNaN(learner.AlphaPeak.Value), "missing alpha histogram is empty");

        // Newer schema: ignored, with a warning.
        state.SchemaVersion = BalanceLearnedState.CurrentSchemaVersion + 1;
        int warnings = log.Warnings.Count;
        learner.Load(state);
        Assert.True(double.IsNaN(learner.G.Value), "newer schema ignored");
        Assert.Equal(warnings + 1, log.Warnings.Count, "newer schema reported");
    }

    // ------------------------------------------------------------------ ParamResolver

    [Test]
    public void Resolver_DefaultsAndPresets()
    {
        var tuning = new BalanceTuning();
        var resolver = new ParamResolver(tuning);
        var p = new EffectiveParams();

        resolver.Resolve(new BalanceOverrides(), null, string.Empty, string.Empty, double.NaN, p);
        Assert.Equal(BalanceClassPreset.None, p.ClassPreset, "no preset");
        AssertParam(tuning.GDefault, ParamSource.Default, p.G, p.GSource, "G default");
        AssertParam(tuning.KDefault, ParamSource.Default, p.K, p.KSource, "K default");
        AssertParam(0.0, ParamSource.Default, p.Theta0, p.Theta0Source, "theta0 default");
        AssertParam(tuning.TauYawDefault, ParamSource.Default, p.TauYaw, p.TauSource, "tau default");
        Assert.True(!p.AyMaxLearned && !p.AlphaPeakLearned, "nothing learned");

        // G from the steering lock: delta_max / (theta_max · L_default).
        const double HalfLock = 4.7;
        resolver.Resolve(new BalanceOverrides(), null, string.Empty, string.Empty, HalfLock, p);
        AssertParam(0.44 / (HalfLock * 2.7), ParamSource.Default, p.G, p.GSource, "G from half-lock");

        // Class preset (LMP3 → Formula/Prototype: L = 3.0, K = 0.0008).
        resolver.Resolve(new BalanceOverrides(), null, "LMP3", "Ligier JS P320", double.NaN, p);
        Assert.Equal(BalanceClassPreset.FormulaPrototype, p.ClassPreset, "auto-detected");
        AssertParam(1.0 / (14.0 * 3.0), ParamSource.Preset, p.G, p.GSource, "G from preset wheelbase");
        AssertParam(0.0008, ParamSource.Preset, p.K, p.KSource, "K from preset");
        resolver.Resolve(new BalanceOverrides(), null, "LMP3", "Ligier JS P320", HalfLock, p);
        AssertParam(0.44 / (HalfLock * 3.0), ParamSource.Preset, p.G, p.GSource, "G from half-lock and preset wheelbase");
    }

    [Test]
    public void Resolver_Precedence_ManualSessionLearnedPresetDefault()
    {
        var tuning = new BalanceTuning();
        var resolver = new ParamResolver(tuning);
        var p = new EffectiveParams();
        var overrides = new BalanceOverrides();
        BalanceLearner learner = TrainedLearner(tuning, 0.03, 0.002, 0.02, 0.15);

        // Learned beats preset.
        resolver.Resolve(overrides, learner, "GT3", "Ferrari 296 GT3", double.NaN, p);
        AssertParam(learner.G.Value, ParamSource.Learned, p.G, p.GSource, "G learned");
        AssertParam(learner.K.Value, ParamSource.Learned, p.K, p.KSource, "K learned");
        AssertParam(learner.Theta0.Value, ParamSource.Learned, p.Theta0, p.Theta0Source, "theta0 learned");
        AssertParam(learner.TauYaw.Value, ParamSource.Learned, p.TauYaw, p.TauSource, "tau learned");
        Assert.Near(0.03, p.G, 0.03 * 0.03, "learned G value");
        Assert.Near(0.15, p.TauYaw, 0.02, "learned tau value");
        Assert.Equal(learner.G.Confidence, p.ConfG, "confidence passed through");

        // Learned values below the confidence threshold fall back to the preset (θ0 with 400 samples has confidence 1).
        tuning.LearnedConfidenceThreshold = 0.999;
        resolver.Resolve(overrides, learner, "GT3", "Ferrari 296 GT3", double.NaN, p);
        Assert.Equal(ParamSource.Preset, p.GSource, "G below threshold");
        Assert.Equal(ParamSource.Preset, p.KSource, "K below threshold");
        Assert.Equal(ParamSource.Learned, p.Theta0Source, "theta0 at confidence 1 is not below the threshold");
        tuning.LearnedConfidenceThreshold = new BalanceTuning().LearnedConfidenceThreshold;

        var young = new BalanceLearner(tuning, NullLog.Instance);
        FeedStraight(young, new Random(79), 0.02, 150);
        resolver.Resolve(overrides, young, "GT3", "Ferrari 296 GT3", double.NaN, p);
        Assert.Equal(ParamSource.Default, p.Theta0Source, "theta0 at confidence 0.5");
        Assert.Equal(ParamSource.Default, p.TauSource, "tau without events");

        // Session beats learned: next session, changed gain, then bent steering.
        learner.ResetSession();
        new ModelSampler(81, 0.03 * 1.25, 0.002).Feed(learner, 3000);
        Assert.True(learner.SessionGActive, "session G override active");
        resolver.Resolve(overrides, learner, "GT3", "Ferrari 296 GT3", double.NaN, p);
        AssertParam(learner.SessionG, ParamSource.Session, p.G, p.GSource, "G session");

        FeedStraight(learner, new Random(80), 0.02 + (4.0 * MathUtil.DegToRad), 600);
        Assert.True(learner.SessionTheta0Active, "session theta0 override active");
        resolver.Resolve(overrides, learner, "GT3", "Ferrari 296 GT3", double.NaN, p);
        AssertParam(learner.SessionTheta0, ParamSource.Session, p.Theta0, p.Theta0Source, "theta0 session");

        // Manual beats everything.
        overrides.G = 0.021;
        overrides.K = 0.0017;
        overrides.Theta0Deg = -2.0;
        overrides.TauYawS = 0.2;
        resolver.Resolve(overrides, learner, "GT3", "Ferrari 296 GT3", double.NaN, p);
        AssertParam(0.021, ParamSource.Manual, p.G, p.GSource, "G manual");
        AssertParam(0.0017, ParamSource.Manual, p.K, p.KSource, "K manual");
        AssertParam(-2.0 * MathUtil.DegToRad, ParamSource.Manual, p.Theta0, p.Theta0Source, "theta0 manual (deg → rad)");
        AssertParam(0.2, ParamSource.Manual, p.TauYaw, p.TauSource, "tau manual");

        // G from a known steering ratio and wheelbase; ratio alone uses the preset wheelbase (GT: 2.7 m).
        overrides.G = null;
        overrides.SteeringRatio = 15.0;
        overrides.WheelbaseM = 2.8;
        resolver.Resolve(overrides, learner, "GT3", "Ferrari 296 GT3", double.NaN, p);
        AssertParam(1.0 / (15.0 * 2.8), ParamSource.Manual, p.G, p.GSource, "G from ratio and wheelbase");
        overrides.WheelbaseM = null;
        resolver.Resolve(overrides, learner, "GT3", "Ferrari 296 GT3", double.NaN, p);
        AssertParam(1.0 / (15.0 * 2.7), ParamSource.Manual, p.G, p.GSource, "G from ratio and preset wheelbase");

        // Non-finite overrides are ignored.
        overrides.SteeringRatio = double.NaN;
        overrides.K = double.PositiveInfinity;
        resolver.Resolve(overrides, learner, "GT3", "Ferrari 296 GT3", double.NaN, p);
        Assert.True(p.GSource != ParamSource.Manual, "NaN ratio ignored");
        Assert.True(p.KSource != ParamSource.Manual, "infinite K ignored");
    }

    [Test]
    public void Resolver_PassesTheNonLearnedKToTheLearner()
    {
        var tuning = new BalanceTuning();
        var resolver = new ParamResolver(tuning);
        var p = new EffectiveParams();
        var learner = new BalanceLearner(tuning, NullLog.Instance);

        resolver.Resolve(new BalanceOverrides(), learner, "LMP3", string.Empty, double.NaN, p);
        Assert.Equal(0.0008, learner.KPrior, "preset K");
        Assert.False(learner.KPriorIsManual, "not manual");

        resolver.Resolve(new BalanceOverrides { K = 0.0021 }, learner, "LMP3", string.Empty, double.NaN, p);
        Assert.Equal(0.0021, learner.KPrior, "manual K");
        Assert.True(learner.KPriorIsManual, "manual");

        resolver.Resolve(new BalanceOverrides(), learner, string.Empty, string.Empty, double.NaN, p);
        Assert.Equal(tuning.KDefault, learner.KPrior, "default K");

        // A manual K also fixes the K the learner fits G with, so G and K stay a consistent pair.
        BalanceLearner trained = TrainedLearner(tuning, 0.03, 0.002, 0.0, 0.15);
        resolver.Resolve(new BalanceOverrides(), trained, string.Empty, string.Empty, double.NaN, p);
        Assert.True(p.GSource == ParamSource.Learned && p.KSource == ParamSource.Learned, "learned pair");
        double learnedG = p.G;
        double learnedK = p.K;
        double learnedConfidence = trained.G.Confidence;

        resolver.Resolve(new BalanceOverrides { K = learnedK }, trained, string.Empty, string.Empty, double.NaN, p);
        Assert.Equal(ParamSource.Manual, p.KSource, "manual K");
        Assert.Equal(ParamSource.Learned, p.GSource, "G still learned with a consistent manual K");
        Assert.Near(learnedG, p.G, 0.005 * learnedG, "same K, same G");

        // A manual K the data contradicts: G is refitted with it (lower, to match at the driven speeds) and its
        // confidence drops because the model no longer fits across the speed range.
        resolver.Resolve(new BalanceOverrides { K = 0.0005 }, trained, string.Empty, string.Empty, double.NaN, p);
        Assert.True(trained.G.Value < 0.9 * learnedG, "G refitted with the manual K: " + trained.G.Value);
        Assert.True(trained.G.Confidence < learnedConfidence, "confidence reflects the misfit");
    }

    [Test]
    public void Resolver_ClassPresetDetectionOverrideAndBetaMargins()
    {
        var tuning = new BalanceTuning();
        var resolver = new ParamResolver(tuning);
        var p = new EffectiveParams();
        var overrides = new BalanceOverrides();

        resolver.Resolve(overrides, null, string.Empty, "Porsche 911 GT3 R", double.NaN, p);
        Assert.Equal(BalanceClassPreset.GT, p.ClassPreset, "detected from the car name");
        Assert.Near(1.5, p.BetaOnsetMarginDeg, 1e-12, "GT onset margin");
        Assert.Near(8.0, p.BetaFullMarginDeg, 1e-12, "GT full margin");

        resolver.Resolve(overrides, null, "LMP3", string.Empty, double.NaN, p);
        Assert.Equal(BalanceClassPreset.FormulaPrototype, p.ClassPreset, "re-detected on car change");
        Assert.Near(1.2, p.BetaOnsetMarginDeg, 1e-12, "prototype onset margin");
        Assert.Near(6.0, p.BetaFullMarginDeg, 1e-12, "prototype full margin");

        overrides.ClassPreset = BalanceClassPreset.RallyLoose;
        resolver.Resolve(overrides, null, "LMP3", string.Empty, double.NaN, p);
        Assert.Equal(BalanceClassPreset.RallyLoose, p.ClassPreset, "override wins");
        Assert.Equal(BalanceClassPreset.FormulaPrototype, p.AutoClassPreset, "detection still reported");
        Assert.Near(tuning.BetaOnsetMarginDeg * 2.5, p.BetaOnsetMarginDeg, 1e-12, "rally onset = global × 2.5");
        Assert.Near(tuning.BetaFullMarginDeg * 2.5, p.BetaFullMarginDeg, 1e-12, "rally full = global × 2.5");
        AssertParam(1.0 / (14.0 * 2.5), ParamSource.Preset, p.G, p.GSource, "rally wheelbase");

        overrides.ClassPreset = BalanceClassPreset.None;
        resolver.Resolve(overrides, null, "LMP3", string.Empty, double.NaN, p);
        Assert.Equal(BalanceClassPreset.None, p.ClassPreset, "explicit none");
        Assert.Near(tuning.BetaOnsetMarginDeg, p.BetaOnsetMarginDeg, 1e-12, "global margins");
        AssertParam(tuning.KDefault, ParamSource.Default, p.K, p.KSource, "no preset K");

        overrides.ClassPreset = BalanceClassPreset.Oval;
        resolver.Resolve(overrides, null, "LMP3", string.Empty, double.NaN, p);
        Assert.Near(tuning.BetaFullMarginDeg, p.BetaFullMarginDeg, 1e-12, "oval has no margins of its own");
        AssertParam(tuning.GDefault, ParamSource.Default, p.G, p.GSource, "oval has no wheelbase");
    }

    // ------------------------------------------------------------------ hot path

    [Test]
    public void SamplePathsAndResolve_DoNotAllocate()
    {
        const int Warmup = 2000;
        const int Measured = 20000;
        const long AllocationSlackBytes = 16 * 1024; // allocation-context granularity of the monitor
        int count = Warmup + Measured;

        // Inputs are precomputed so the measured loop contains learner and resolver calls only.
        var rng = new Random(95);
        var speed = new double[count];
        var steering = new double[count];
        var yawRate = new double[count];
        var noise = new double[count];
        var traceSteadyState = new double[count];
        var traceYaw = new double[count];
        var traceSteeringRate = new double[count];
        double lagged = 0.0;
        double previousSteering = 0.0;
        for (int i = 0; i < count; i++)
        {
            speed[i] = 15.0 + (40.0 * rng.NextDouble());
            double yaw = 0.05 + ((4.0 / speed[i] - 0.05) * rng.NextDouble());
            steering[i] = yaw * (1.0 + (0.0013 * speed[i] * speed[i])) / (0.026 * speed[i]);
            yawRate[i] = yaw * (1.0 + (0.03 * Gaussian(rng)));
            noise[i] = rng.NextDouble();

            // Turn-in every 3 s: 1 s straight, 0.15 s ramp to 0.45 rad, hold, then unwind.
            double phase = (i % 180) * Dt;
            double wheel = phase < 1.0 ? 0.0 : phase < 1.15 ? 3.0 * (phase - 1.0) : phase < 2.2 ? 0.45 : Math.Max(0.0, 0.45 - (3.0 * (phase - 2.2)));
            double steadyState = 0.026 * 30.0 * wheel / (1.0 + (0.0013 * 900.0));
            lagged += (steadyState - lagged) * MathUtil.LagAlpha(Dt, 0.15);
            traceSteadyState[i] = steadyState;
            traceYaw[i] = lagged;
            traceSteeringRate[i] = (wheel - previousSteering) / Dt;
            previousSteering = wheel;
        }

        var tuning = new BalanceTuning();
        var learner = new BalanceLearner(tuning, NullLog.Instance);
        var resolver = new ParamResolver(tuning);
        var overrides = new BalanceOverrides();
        var parameters = new EffectiveParams();
        const string CarClass = "LMP3";
        const string CarKey = "Ligier JS P320";

        void Tick(int i)
        {
            learner.AddModelSample(speed[i], steering[i], yawRate[i], Dt);
            learner.AddStraightSample(0.01 * noise[i], Dt);
            learner.AddLateralSample(18.0 * noise[i]);
            learner.AddBodySlipSample(noise[i], 3.0 * noise[i]);
            learner.AddSlipAngleSample(0.1 * noise[i], 0.09);
            learner.AddYawTrace(i * Dt, Dt, traceSteadyState[i], traceYaw[i], traceSteeringRate[i], 30.0);
            learner.BetaEnvelopeDeg(noise[i]);
            resolver.Resolve(overrides, learner, CarClass, CarKey, 4.7, parameters);
        }

        for (int i = 0; i < Warmup; i++)
        {
            Tick(i);
        }

        AppDomain.MonitoringIsEnabled = true;
        long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        for (int i = Warmup; i < count; i++)
        {
            Tick(i);
        }

        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        Assert.True(allocated <= AllocationSlackBytes, "allocated " + allocated + " bytes in " + Measured + " ticks");
        Assert.True(learner.TauYaw.Samples > 0 && MathUtil.IsFinite(learner.G.Value), "all paths exercised");
    }

    // ------------------------------------------------------------------ building blocks

    [Test]
    public void Rls_FitsExactLinesAndConstrainedModels()
    {
        var rls = new RecursiveLeastSquares(0.0, 0.0, 100.0, 100.0);
        for (int i = 0; i <= 100; i++)
        {
            double x = i * 0.05;
            Assert.True(rls.Update(x, 3.0 + (2.0 * x), 1.0, 1.0), "update");
        }

        Assert.Near(3.0, rls.A, 1e-4, "intercept");
        Assert.Near(2.0, rls.B, 1e-4, "slope");
        Assert.Near(0.0, rls.ResidualSumOfSquares(3.0, 2.0), 1e-9, "exact line has no residual");
        Assert.Near(101.0, rls.ResidualSumOfSquares(4.0, 2.0), 1e-9, "offset line: 101 samples × 1²");
        Assert.True(rls.TrySolveWithFixedRatio(2.0 / 3.0, out double intercept), "constrained fit");
        Assert.Near(3.0, intercept, 1e-12, "constrained intercept");

        Assert.False(rls.Update(double.NaN, 1, 1, 1), "NaN x");
        Assert.False(rls.Update(1, double.PositiveInfinity, 1, 1), "infinite y");
        Assert.False(rls.Update(1, 1, 0, 1), "zero weight");
        Assert.False(rls.Update(1, 1, 1, 0), "lambda 0");
        Assert.False(rls.Update(1, 1, 1, 1.5), "lambda > 1");
        Assert.False(rls.TryLoad(1.0, 10.0, 1.0, 0, 0, 0), "inconsistent sums rejected");
        Assert.True(rls.IsEmpty, "cleared after rejected load");
    }

    [Test]
    public void Rls_CovarianceStaysBoundedWithoutExcitation()
    {
        const double PriorStd = 100.0;
        var rls = new RecursiveLeastSquares(10.0, 10.0, PriorStd, PriorStd);
        for (int i = 0; i < 200000; i++)
        {
            rls.Update(1.0, 5.0, 1.0, 0.995); // x never changes: slope/intercept split is unobservable
        }

        double bound = PriorStd * PriorStd * (1.0 + (5.0 * 5.0));
        double variance = rls.PredictionVariance(5.0);
        Assert.True(MathUtil.IsFinite(variance) && variance > 0 && variance <= bound, "bounded: " + variance);

        // ~200 samples of memory against a weak prior: the observed point is fitted up to a tiny prior pull.
        Assert.Near(5.0, rls.Predict(1.0), 1e-5, "fits the observed point");
        Assert.True(MathUtil.IsFinite(rls.A) && MathUtil.IsFinite(rls.B), "finite");
    }

    [Test]
    public void Histogram_PercentilesInterpolateAndLoadTolerantly()
    {
        Histogram linear = Histogram.Linear(0.0, 10.0, 10, 1000);
        Assert.True(double.IsNaN(linear.Percentile(0.5)), "empty");
        for (int i = 0; i < 10; i++)
        {
            linear.Add(0.5);
            linear.Add(1.5);
        }

        linear.Add(double.NaN);
        Assert.Equal(20L, linear.Total, "NaN ignored");
        Assert.Near(1.0, linear.Percentile(0.5), 1e-12, "median at the bin edge");
        Assert.Near(1.5, linear.Percentile(0.75), 1e-12, "interpolated");
        Assert.Near(0.0, linear.Percentile(0.0), 1e-12, "minimum");
        Assert.Near(2.0, linear.Percentile(1.0), 1e-12, "maximum");
        linear.Add(-5.0);
        linear.Add(50.0);
        Assert.Equal(22L, linear.Total, "out of range counted in the edge bins");

        Histogram log = Histogram.Logarithmic(1e-4, 100.0, 120, 1000);
        log.Add(0.1);
        log.Add(0.0);
        Assert.Equal(1L, log.Total, "non-positive ignored on log bins");
        Assert.Near(Math.Pow(10.0, -4.0 + (61 * 0.05)), log.Percentile(1.0), 1e-12, "upper edge of the 0.1 bin");

        Histogram forgetting = Histogram.Linear(0.0, 1.0, 4, 10);
        for (int i = 0; i < 11; i++)
        {
            forgetting.Add(0.1);
        }

        Assert.Equal(5L, forgetting.Total, "halved above the threshold");

        Assert.True(linear.Load(new[] { 1, 2 }) && linear.Total == 3, "short array");
        Assert.True(linear.Load(null) && linear.Total == 0, "null array");
        Assert.True(linear.Load(new int[20]) && linear.Total == 0, "long array truncated");
        Assert.False(linear.Load(new[] { 1, -1 }), "negative counts rejected");
        Assert.Equal(0L, linear.Total, "cleared after rejection");
        Assert.Equal(0, linear.ToTrimmedArray().Length, "trimmed");
    }

    // ------------------------------------------------------------------ helpers

    private static BalanceLearner Fit(double g, double k)
    {
        var learner = new BalanceLearner(new BalanceTuning(), NullLog.Instance);
        new ModelSampler(90, g, k).Feed(learner, 5000);
        return learner;
    }

    private static double Centre(int loadBin) => (loadBin + 0.5) / BetaEnvelope.LoadBinCount;

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static void FeedStraight(BalanceLearner learner, Random rng, double offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            learner.AddStraightSample(offset + (0.004 * Gaussian(rng)), Dt);
        }
    }

    /// <summary>Interleaves every sample type like a real stint (deterministic per seed).</summary>
    private static void FeedMixed(BalanceLearner learner, int seed, double g, double k, double theta0, double tau, int rounds)
    {
        var rng = new Random(seed);
        var model = new ModelSampler(seed + 1, g, k);
        var turnIns = new TurnInGenerator(seed + 2, tau);
        for (int round = 0; round < rounds; round++)
        {
            model.Feed(learner, 250);
            FeedStraight(learner, rng, theta0, 50);
            for (int i = 0; i < 200; i++)
            {
                learner.AddLateralSample(18.0 * rng.NextDouble());
            }

            for (int i = 0; i < 250; i++)
            {
                double ayNorm = rng.NextDouble();
                learner.AddBodySlipSample(ayNorm, (0.5 + (2.5 * ayNorm)) * rng.NextDouble());
            }

            for (int i = 0; i < 60; i++)
            {
                double alpha = 0.1 * (0.5 + (0.5 * rng.NextDouble()));
                learner.AddSlipAngleSample(alpha, 0.9 * alpha);
            }

            turnIns.FeedEvents(learner, 1);
        }
    }

    private static BalanceLearner TrainedLearner(BalanceTuning tuning, double g, double k, double theta0, double tau)
    {
        var learner = new BalanceLearner(tuning, NullLog.Instance);
        new ModelSampler(5, g, k).Feed(learner, 3000);
        FeedStraight(learner, new Random(6), theta0, 400);
        new TurnInGenerator(7, tau).FeedEvents(learner, 20);
        return learner;
    }

    private static void AssertParam(double expectedValue, ParamSource expectedSource, double value, ParamSource source, string message)
    {
        Assert.Equal(expectedSource, source, message + " (source)");
        Assert.Near(expectedValue, value, 1e-12 * Math.Max(1.0, Math.Abs(expectedValue)), message);
    }

    private static void AssertFiniteOrNaN(LearnedParam param, string message)
    {
        Assert.True(double.IsNaN(param.Value) || MathUtil.IsFinite(param.Value), message + ": infinite value");
        Assert.InRange(param.Confidence, 0.0, 1.0, message + ": confidence");
    }

    private static void AssertSameBaseline(BalanceLearner expected, BalanceLearner actual, string context)
    {
        AssertSameParam(expected.G, actual.G, context + " G");
        AssertSameParam(expected.K, actual.K, context + " K");
        AssertSameParam(expected.Theta0, actual.Theta0, context + " theta0");
        AssertSameParam(expected.TauYaw, actual.TauYaw, context + " tau");
        AssertSameParam(expected.AyMax, actual.AyMax, context + " ay_max");
        AssertSameParam(expected.AlphaPeak, actual.AlphaPeak, context + " alpha_peak");
        Assert.Near(expected.VSpread, actual.VSpread, RoundTripTolerance * Math.Max(1.0, expected.VSpread), context + " spread");
        Assert.Equal(expected.RejectedModelSamples, actual.RejectedModelSamples, context + " rejected");
        Assert.Equal(expected.BetaEnvelopeLearnedBins, actual.BetaEnvelopeLearnedBins, context + " envelope bins");
        for (int i = 0; i <= 20; i++)
        {
            double ayNorm = i / 20.0;
            Assert.Near(expected.BetaEnvelopeDeg(ayNorm), actual.BetaEnvelopeDeg(ayNorm), RoundTripTolerance, context + " envelope");
        }
    }

    private static void AssertSameParam(LearnedParam expected, LearnedParam actual, string message)
    {
        Assert.Near(expected.Value, actual.Value, RoundTripTolerance * Math.Max(1e-3, Math.Abs(expected.Value)), message + " value");
        Assert.Near(expected.Confidence, actual.Confidence, RoundTripTolerance, message + " confidence");
        Assert.Equal(expected.Samples, actual.Samples, message + " samples");
    }

    /// <summary>
    /// Quasi-steady cornering samples inside the estimator's learning gates (|r| &gt; 0.05 rad/s, |ay| &lt; 4 m/s²,
    /// |θeff| &gt; 2°) for a car with known G and K, with relative yaw-rate noise, steering noise and gross outliers.
    /// </summary>
    private sealed class ModelSampler
    {
        private const double YawRateFloor = 0.05;
        private const double MaxLinearAy = 4.0;
        private const double MinSteering = 2.0 * MathUtil.DegToRad;

        private readonly Random rng;
        private readonly double gain;
        private readonly double understeer;

        public ModelSampler(int seed, double gain, double understeer)
        {
            rng = new Random(seed);
            this.gain = gain;
            this.understeer = understeer;
        }

        public double MinSpeed { get; set; } = 10.0;

        public double MaxSpeed { get; set; } = 70.0;

        public double YawNoise { get; set; } = 0.03;

        public double SteeringNoise { get; set; } = 0.001;

        public double OutlierFraction { get; set; } = 0.05;

        /// <summary>True steering offset added to the raw steering (rad).</summary>
        public double SteeringOffset { get; set; }

        /// <summary>The θ0 the "estimator" subtracts (defaults to the true offset: θeff is exact).</summary>
        public Func<double> OffsetInUse { get; set; }

        public void Feed(BalanceLearner learner, int count)
        {
            for (int i = 0; i < count; i++)
            {
                FeedOne(learner);
            }
        }

        public void FeedOne(BalanceLearner learner)
        {
            while (true)
            {
                double v = MinSpeed + (rng.NextDouble() * (MaxSpeed - MinSpeed));
                double maxYaw = MaxLinearAy / v;
                if (maxYaw <= YawRateFloor)
                {
                    continue;
                }

                double yaw = YawRateFloor + (rng.NextDouble() * (maxYaw - YawRateFloor));
                double steering = yaw * (1.0 + (understeer * v * v)) / (gain * v);
                if (steering < MinSteering)
                {
                    continue;
                }

                double sign = rng.Next(2) == 0 ? -1.0 : 1.0;
                double rawSteering = (sign * (steering + (SteeringNoise * Gaussian(rng)))) + SteeringOffset;
                double measuredSteering = rawSteering - (OffsetInUse != null ? OffsetInUse() : SteeringOffset);
                double measuredYaw = sign * yaw * (1.0 + (YawNoise * Gaussian(rng)));
                if (rng.NextDouble() < OutlierFraction)
                {
                    measuredYaw *= rng.Next(2) == 0 ? 1.5 + (2.5 * rng.NextDouble()) : 0.2 + (0.4 * rng.NextDouble());
                }

                learner.AddModelSample(v, measuredSteering, measuredYaw, Dt);
                return;
            }
        }
    }

    /// <summary>
    /// Drives straight, turns in quickly, holds the corner and unwinds, at constant speed; the yaw rate follows the
    /// steady-state yaw rate through the same first-order lag (time constant τ) the estimator uses.
    /// </summary>
    private sealed class TurnInGenerator
    {
        private const double Gain = 0.026;
        private const double Understeer = 0.0013;
        private const double Speed = 30.0;

        private readonly Random rng;
        private readonly double tau;
        private double time;
        private double steering;
        private double yaw;
        private int ticksInTurn;

        public TurnInGenerator(int seed, double tau)
        {
            rng = new Random(seed);
            this.tau = tau;
        }

        /// <summary>Standard deviation of the yaw-rate measurement noise (rad/s).</summary>
        public double YawNoise { get; set; }

        /// <summary>Factor on the r_ss handed to the learner (a not-yet-learned gain).</summary>
        public double ModelGainError { get; set; } = 1.0;

        /// <summary>When &gt; 0, calls AbortYawEvent this many ticks into every turn-in.</summary>
        public int AbortAfterTicks { get; set; }

        public void FeedEvents(BalanceLearner learner, int events)
        {
            for (int e = 0; e < events; e++)
            {
                double direction = e % 2 == 0 ? 1.0 : -1.0;
                double target = direction * (0.3 + (0.4 * rng.NextDouble()));
                double rate = 2.0 + (2.0 * rng.NextDouble());
                Hold(learner, 1.0);
                ticksInTurn = 0;
                Ramp(learner, target, rate);
                Hold(learner, 1.0);
                Ramp(learner, 0.0, rate);
            }

            Hold(learner, 1.0);
        }

        private void Ramp(BalanceLearner learner, double target, double rate)
        {
            double step = rate * Dt;
            while (steering != target)
            {
                double previous = steering;
                double remaining = target - steering;
                steering = Math.Abs(remaining) <= step ? target : steering + (Math.Sign(remaining) * step);
                Tick(learner, previous);
            }
        }

        private void Hold(BalanceLearner learner, double seconds)
        {
            int ticks = (int)Math.Round(seconds / Dt);
            for (int i = 0; i < ticks; i++)
            {
                Tick(learner, steering);
            }
        }

        private void Tick(BalanceLearner learner, double previousSteering)
        {
            time += Dt;
            double steadyState = Gain * Speed * steering / (1.0 + (Understeer * Speed * Speed));
            yaw += (steadyState - yaw) * MathUtil.LagAlpha(Dt, tau);
            double measured = yaw + (YawNoise * Gaussian(rng));
            learner.AddYawTrace(time, Dt, steadyState * ModelGainError, measured, (steering - previousSteering) / Dt, Speed);

            // From the given tick on, every tick aborts, so an event re-triggered later in the same turn-in is dropped too.
            ticksInTurn++;
            if (AbortAfterTicks > 0 && ticksInTurn >= AbortAfterTicks)
            {
                learner.AbortYawEvent();
            }
        }
    }

    /// <summary>ILog that records warnings.</summary>
    private sealed class RecordingLog : ILog
    {
        public List<string> Warnings { get; } = new List<string>();

        public void Info(string message)
        {
        }

        public void Warn(string message) => Warnings.Add(message);

        public void Error(string message) => Warnings.Add(message);
    }
}
