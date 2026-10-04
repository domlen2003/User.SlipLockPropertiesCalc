using System.Text;
using DivebombLogistics.Core;
using DivebombLogistics.Core.Telemetry;
using DivebombLogistics.Haptics.Settings;
using DivebombLogistics.Haptics.Telemetry;

namespace DivebombLogistics.Tests;

/// <summary>Tests for the Telemetry module: slip source resolution, wheel-speed detection, capabilities, car identity.</summary>
internal sealed class TelemetryTests
{
    private const double Tolerance = 1e-9;
    private const string Game = "TestGame";

    /// <summary>Stand-in for LMU's boxed <c>IP_VehicleClass</c> enum.</summary>
    private enum FakeVehicleClass
    {
        Hypercar,
        LMP3,
    }

    // ------------------------------------------------------------------ SlipSourceResolver

    [Test]
    public void Resolver_PrefersShakeItWheelSlipOverProxyAndAcc()
    {
        var reader = new FakeTelemetryReader();
        SetAll(reader, PropertyPaths.AccWheelSlip, 0.5);
        SetAll(reader, PropertyPaths.ShakeItProxySlip, 7.0);
        SetAll(reader, PropertyPaths.ShakeItWheelSlip, 42.0);
        var resolver = new SlipSourceResolver();

        Assert.True(resolver.Probe(reader, 0.0), "probe reports the new resolution");
        Assert.Equal(SlipSourceKind.ShakeIt, resolver.Kind);
        Assert.False(resolver.IsSigned, "ShakeIT is unsigned");
        for (int i = 0; i < Wheels.Count; i++)
        {
            Assert.Equal(PropertyPaths.ShakeItWheelSlip[i], resolver.ResolvedPaths[i], "path " + i);
        }

        var dest = new double[Wheels.Count];
        Assert.True(resolver.TryRead(reader, 30.0, dest));
        Assert.Near(42.0, dest[Wheels.RearRight], Tolerance, "ShakeIT values are passed through");
    }

    [Test]
    public void Resolver_MixesCandidatesPerWheelAndClassifiesByFrontLeft()
    {
        var reader = new FakeTelemetryReader()
            .Set(PropertyPaths.ShakeItWheelSlip[0], 10.0)
            .Set(PropertyPaths.ShakeItProxySlip[1], 20.0)
            .Set(PropertyPaths.AccWheelSlip[2], 1.0)
            .Set(PropertyPaths.ShakeItWheelSlip[3], 40.0);
        var resolver = new SlipSourceResolver();
        resolver.Probe(reader, 0.0);

        Assert.Equal(SlipSourceKind.ShakeIt, resolver.Kind, "v1 classifies by the FL path");
        Assert.Equal(PropertyPaths.ShakeItProxySlip[1], resolver.ResolvedPaths[1]);
        Assert.Equal(PropertyPaths.AccWheelSlip[2], resolver.ResolvedPaths[2]);

        var dest = new double[Wheels.Count];
        Assert.True(resolver.TryRead(reader, 30.0, dest));
        Assert.Near(1.0, dest[2], Tolerance, "mixed ACC wheel follows FL's (ShakeIT) scaling like v1");
    }

    [Test]
    public void Resolver_UnresolvedWhenAnyWheelHasNoCandidate()
    {
        var reader = new FakeTelemetryReader();
        SetAll(reader, PropertyPaths.ShakeItWheelSlip, 1.0);
        reader.Remove(PropertyPaths.ShakeItWheelSlip[Wheels.RearLeft]);
        var resolver = new SlipSourceResolver();

        Assert.False(resolver.Probe(reader, 0.0));
        Assert.Equal(SlipSourceKind.None, resolver.Kind);
        for (int i = 0; i < Wheels.Count; i++)
        {
            Assert.Equal<string>(null, resolver.ResolvedPaths[i], "no partial paths kept");
        }

        Assert.False(resolver.TryRead(reader, 30.0, new double[Wheels.Count]));
    }

    [Test]
    public void Resolver_AccNativeScalesBy20AndClampsAt100()
    {
        var reader = new FakeTelemetryReader()
            .Set(PropertyPaths.AccWheelSlip[0], 0.5)
            .Set(PropertyPaths.AccWheelSlip[1], 7.0)
            .Set(PropertyPaths.AccWheelSlip[2], -1.0)
            .Set(PropertyPaths.AccWheelSlip[3], 5.0);
        var resolver = new SlipSourceResolver();
        resolver.Probe(reader, 0.0);
        Assert.Equal(SlipSourceKind.AccNative, resolver.Kind);

        var dest = new double[Wheels.Count];
        Assert.True(resolver.TryRead(reader, 30.0, dest));
        Assert.Near(10.0, dest[0], Tolerance, "0.5 x 20");
        Assert.Near(100.0, dest[1], Tolerance, "clamped to 100");
        Assert.Near(-20.0, dest[2], Tolerance, "v1 has no lower clamp");
        Assert.Near(100.0, dest[3], Tolerance, "5 x 20");
    }

    [Test]
    public void Resolver_RotationSlipFormulaAndRadiusHandling()
    {
        var reader = new FakeTelemetryReader();
        SetAll(reader, PropertyPaths.RFactorWheelRotation, 60.0);
        reader.Set(PropertyPaths.RFactorWheelRotation[1], -60.0);
        reader.Set(PropertyPaths.RFactorWheelRotation[3], 0.0);
        reader.Set(PropertyPaths.RFactorWheelRadius[0], (byte)34);
        reader.Set(PropertyPaths.RFactorWheelRadius[1], (byte)34);
        reader.Set(PropertyPaths.RFactorWheelRadius[2], 0.35f);

        // RR radius missing -> default.
        var resolver = new SlipSourceResolver();
        resolver.Probe(reader, 0.0);

        Assert.Equal(SlipSourceKind.RFactorRotation, resolver.Kind);
        Assert.True(resolver.IsSigned, "rotation is signed");
        Assert.Near(0.34, resolver.GetTireRadius(0), Tolerance, "byte centimetres -> metres");
        Assert.Near(0.35, resolver.GetTireRadius(2), 1e-6, "values <= 1 are metres");
        Assert.Near(SlipSourceResolver.DefaultTireRadiusM, resolver.GetTireRadius(3), Tolerance, "missing -> 0.33 m");

        var dest = new double[Wheels.Count];
        const double vs = 20.0;
        Assert.True(resolver.TryRead(reader, vs, dest));
        double expectedFront = ((System.Math.Abs(60.0) * 0.34) - vs) / vs * 100.0;
        Assert.Near(expectedFront, dest[0], Tolerance, "(|w| r - v) / v * 100");
        Assert.Near(expectedFront, dest[1], Tolerance, "rotation sign is ignored (|w|)");
        Assert.Near(((60.0 * (double)0.35f) - vs) / vs * 100.0, dest[2], 1e-9);
        Assert.Near(-100.0, dest[3], Tolerance, "stopped wheel = full lock, clamped to -100");

        SetAll(reader, PropertyPaths.RFactorWheelRotation, 1000.0);
        Assert.True(resolver.TryRead(reader, vs, dest));
        Assert.Near(100.0, dest[0], Tolerance, "clamped to +100");
    }

    [Test]
    public void Resolver_RotationBelowOneMeterPerSecondIsZeroWithoutReads()
    {
        var reader = new FakeTelemetryReader();
        SetAll(reader, PropertyPaths.RFactorWheelRotation, 5.0);
        var resolver = new SlipSourceResolver();
        resolver.Probe(reader, 0.0);

        var dest = new double[] { 9, 9, 9, 9 };
        int reads = reader.ReadCount;
        Assert.True(resolver.TryRead(reader, 0.99, dest), "v1 reports success with zeros");
        Assert.Equal(reads, reader.ReadCount, "no property reads below 1 m/s");
        for (int i = 0; i < Wheels.Count; i++)
        {
            Assert.Near(0.0, dest[i], 0.0, "zero below 1 m/s");
        }

        reader.Remove(PropertyPaths.RFactorWheelRotation[2]);
        Assert.False(resolver.TryRead(reader, 10.0, dest), "missing wheel fails the read");
    }

    [Test]
    public void Resolver_ShakeItBeatsRotationButRotationIsKeptOnceChosen()
    {
        var reader = new FakeTelemetryReader();
        SetAll(reader, PropertyPaths.RFactorWheelRotation, 5.0);
        SetAll(reader, PropertyPaths.ShakeItWheelSlip, 3.0);
        var resolver = new SlipSourceResolver();
        resolver.Probe(reader, 0.0);
        Assert.Equal(SlipSourceKind.ShakeIt, resolver.Kind, "standard candidates first");

        reader.Clear();
        SetAll(reader, PropertyPaths.RFactorWheelRotation, 5.0);
        resolver.Reset();
        resolver.Probe(reader, 10.0);
        Assert.Equal(SlipSourceKind.RFactorRotation, resolver.Kind);

        SetAll(reader, PropertyPaths.ShakeItWheelSlip, 3.0);
        Assert.False(resolver.Probe(reader, 20.0), "resolved sources are not re-probed");
        Assert.Equal(SlipSourceKind.RFactorRotation, resolver.Kind, "v1 keeps rotation until reset");
    }

    [Test]
    public void Resolver_ProbingIsRateLimited()
    {
        var reader = new FakeTelemetryReader();
        var resolver = new SlipSourceResolver();

        resolver.Probe(reader, 100.0);
        int readsAfterFirst = reader.ReadCount;
        Assert.True(readsAfterFirst > 0, "first probe runs immediately");

        SetAll(reader, PropertyPaths.ShakeItWheelSlip, 1.0);
        resolver.Probe(reader, 100.2);
        resolver.Probe(reader, 100.49);
        Assert.Equal(readsAfterFirst, reader.ReadCount, "no lookups inside the 0.5 s window");
        Assert.Equal(SlipSourceKind.None, resolver.Kind);

        Assert.True(resolver.Probe(reader, 100.5), "next probe after 0.5 s");
        Assert.Equal(SlipSourceKind.ShakeIt, resolver.Kind);

        resolver.Reset();
        int readsBeforeReset = reader.ReadCount;
        resolver.Probe(reader, 100.6);
        Assert.True(reader.ReadCount > readsBeforeReset, "reset probes immediately");
    }

    [Test]
    public void Resolver_WheelLockAppearingLaterIsPickedUpEveryTwoSeconds()
    {
        var reader = new FakeTelemetryReader();
        SetAll(reader, PropertyPaths.ShakeItWheelSlip, 1.0);
        var resolver = new SlipSourceResolver();
        var dest = new double[] { 5, 5, 5, 5 };

        resolver.Probe(reader, 0.0);
        Assert.False(resolver.HasWheelLock);
        Assert.False(resolver.ReadWheelLock(reader, dest));
        Assert.Near(0.0, dest[0], 0.0, "dest zeroed on failure");

        SetAll(reader, PropertyPaths.ShakeItWheelLock, 12.5);
        Assert.False(resolver.Probe(reader, 1.9), "still inside the 2 s window");
        Assert.False(resolver.HasWheelLock);

        Assert.True(resolver.Probe(reader, 2.0));
        Assert.True(resolver.HasWheelLock);
        Assert.Equal(PropertyPaths.ShakeItWheelLock[3], resolver.WheelLockPaths[3]);
        Assert.True(resolver.ReadWheelLock(reader, dest));
        Assert.Near(12.5, dest[3], Tolerance);

        reader.Set(PropertyPaths.ShakeItWheelLock[1], double.NaN);
        Assert.True(resolver.ReadWheelLock(reader, dest));
        Assert.Near(0.0, dest[1], 0.0, "non-finite values read as 0");

        reader.Remove(PropertyPaths.ShakeItWheelLock[2]);
        Assert.False(resolver.ReadWheelLock(reader, dest), "disappearing export fails the read");
        Assert.Near(0.0, dest[3], 0.0);
    }

    // ------------------------------------------------------------------ WheelSpeedModeDetector

    [Test]
    public void Detector_NoWheelSpeedDecidesMonoImmediately()
    {
        var fixture = new DetectorFixture();
        SetAll(fixture.Reader, PropertyPaths.ShakeItWheelSlip, 8.0);
        fixture.Frame(vs: 0.0, sway: 0.0, brake: 0.0);

        Assert.Equal(DetectionState.Mono, fixture.Detector.State);
        Assert.Equal(GameCapabilities.ModeMono, fixture.Settings.GameCapabilities[Game].WheelSpeedMode);
        Assert.True(fixture.Detector.SettingsChanged, "settings change flagged");
        Assert.Near(8.0, fixture.BaseSlip[0], Tolerance, "the deciding frame still outputs the slip source");

        fixture.Detector.ClearSettingsChanged();
        fixture.Frame(vs: 30.0, sway: 5.0, brake: 0.0);
        Assert.False(fixture.Detector.SettingsChanged);
        Assert.Near(8.0, fixture.BaseSlip[2], Tolerance, "mono = slip source values");
        Assert.False(fixture.Detector.BaseSlipIsSigned, "ShakeIT mono is unsigned");
        Assert.Equal(SlipSourceKind.ShakeIt, fixture.Detector.EffectiveSource);
    }

    [Test]
    public void Detector_WheelSpeedDifferenceDecidesPerWheel()
    {
        var fixture = new DetectorFixture();
        SetWheelSpeeds(fixture.Reader, 30.0, 30.0, 30.0, 30.0);

        fixture.Frame(vs: 30.0, sway: 0.1, brake: 0.0);
        Assert.Equal(DetectionState.Detecting, fixture.Detector.State, "not dynamic -> keep waiting");
        Assert.True(fixture.Detector.DetectSpeedOk);
        Assert.False(fixture.Detector.DetectCornerOk);
        Assert.False(fixture.Detector.DetectBrakeOk);
        Assert.Equal(0, fixture.Detector.DetectFrames, "non-dynamic frames are not counted");

        SetWheelSpeeds(fixture.Reader, 30.0, 30.2, 29.9, 30.0);
        fixture.Frame(vs: 30.0, sway: 2.0, brake: 0.0);
        Assert.Equal(DetectionState.PerWheel, fixture.Detector.State);
        Assert.Near(0.2, fixture.Detector.DetectMaxDelta, 1e-9);
        Assert.Equal(GameCapabilities.ModePerWheel, fixture.Settings.GameCapabilities[Game].WheelSpeedMode);

        SetWheelSpeeds(fixture.Reader, 33.0, 30.0, 27.0, 30.0);
        fixture.Frame(vs: 30.0, sway: 0.0, brake: 0.0);
        Assert.True(fixture.Detector.BaseSlipIsSigned, "per-wheel is signed");
        Assert.Equal(SlipSourceKind.PerWheelSpeed, fixture.Detector.EffectiveSource);
        Assert.Near((33.0 - 30.0) / 30.0 * 100.0, fixture.BaseSlip[0], Tolerance);
        Assert.Near((27.0 - 30.0) / 30.0 * 100.0, fixture.BaseSlip[2], Tolerance);

        fixture.Frame(vs: 0.9, sway: 0.0, brake: 0.0);
        Assert.Near(0.0, fixture.BaseSlip[0], 0.0, "zero at or below 1 m/s");

        SetWheelSpeeds(fixture.Reader, 300.0, 30.0, 30.0, 30.0);
        fixture.Frame(vs: 30.0, sway: 0.0, brake: 0.0);
        Assert.Near(100.0, fixture.BaseSlip[0], 0.0, "clamped to 100");
    }

    [Test]
    public void Detector_EqualWheelSpeedsForSixtyDynamicFramesDecideMono()
    {
        var fixture = new DetectorFixture();

        // Just below the threshold: no per-wheel difference (the rule is strictly greater than).
        SetWheelSpeeds(fixture.Reader, 30.0, 30.0 + WheelSpeedModeDetector.PerWheelDeltaThreshold - 1e-12, 30.0, 30.0);
        for (int frame = 1; frame < WheelSpeedModeDetector.MonoConfirmFrames; frame++)
        {
            fixture.Frame(vs: 30.0, sway: 0.0, brake: 50.0);
            Assert.Equal(DetectionState.Detecting, fixture.Detector.State, "frame " + frame);
            Assert.Equal(frame, fixture.Detector.DetectFrames);
        }

        Assert.True(fixture.Detector.DetectBrakeOk);
        fixture.Frame(vs: 30.0, sway: 0.0, brake: 50.0);
        Assert.Equal(DetectionState.Mono, fixture.Detector.State, "decided after 60 dynamic frames");
        Assert.Equal(GameCapabilities.ModeMono, fixture.Settings.GameCapabilities[Game].WheelSpeedMode);
    }

    [Test]
    public void Detector_DetectingOutputsSlipSourceAndItsSignedness()
    {
        var fixture = new DetectorFixture();
        SetWheelSpeeds(fixture.Reader, 30.0, 30.0, 30.0, 30.0);
        SetAll(fixture.Reader, PropertyPaths.RFactorWheelRotation, 100.0);
        fixture.Frame(vs: 30.0, sway: 0.0, brake: 0.0);

        Assert.Equal(DetectionState.Detecting, fixture.Detector.State);
        Assert.Equal(SlipSourceKind.RFactorRotation, fixture.Detector.EffectiveSource);
        Assert.True(fixture.Detector.BaseSlipIsSigned, "rotation source is signed");
        Assert.True(fixture.Detector.SlipSourceAvailable);
        Assert.Near(((100.0 * 0.33) - 30.0) / 30.0 * 100.0, fixture.BaseSlip[0], Tolerance);
    }

    [Test]
    public void Detector_WithoutAnySlipSourceOutputsZeros()
    {
        var fixture = new DetectorFixture();
        fixture.Frame(vs: 30.0, sway: 0.0, brake: 0.0);
        Assert.Equal(DetectionState.Mono, fixture.Detector.State);
        Assert.False(fixture.Detector.SlipSourceAvailable);
        for (int i = 0; i < Wheels.Count; i++)
        {
            Assert.Near(0.0, fixture.BaseSlip[i], 0.0);
        }
    }

    [Test]
    public void Detector_LoadsPersistedModesAndCapabilityFlags()
    {
        var fixture = new DetectorFixture();
        fixture.Settings.GameCapabilities[Game] = new GameCapabilities
        {
            WheelSpeedMode = GameCapabilities.ModePerWheel,
            ABSMode = GameCapabilities.Available,
        };
        fixture.Frame(vs: 30.0, sway: 0.0, brake: 0.0);

        Assert.Equal(DetectionState.PerWheel, fixture.Detector.State);
        Assert.True(fixture.Capabilities.AbsEverActive, "persisted ABS Available");
        Assert.False(fixture.Capabilities.TcEverActive);
        Assert.False(fixture.Detector.SettingsChanged, "loading does not change settings");

        fixture.Settings.GameCapabilities[Game] = new GameCapabilities
        {
            WheelSpeedMode = GameCapabilities.ModeMono,
            TCMode = GameCapabilities.Available,
        };
        fixture.ChangeGame(Game);
        fixture.Frame(vs: 30.0, sway: 0.0, brake: 0.0);
        Assert.Equal(DetectionState.Mono, fixture.Detector.State);
        Assert.False(fixture.Capabilities.AbsEverActive, "reset on game change, not persisted");
        Assert.True(fixture.Capabilities.TcEverActive);

        fixture.Settings.GameCapabilities[Game] = new GameCapabilities();
        fixture.ChangeGame(Game);
        SetWheelSpeeds(fixture.Reader, 30.0, 30.0, 30.0, 30.0);
        fixture.Frame(vs: 30.0, sway: 0.0, brake: 0.0);
        Assert.Equal(DetectionState.Detecting, fixture.Detector.State, "'Unknown' re-detects");
    }

    [Test]
    public void Detector_RetestRemovesEntryAndDetectsAgain()
    {
        var fixture = new DetectorFixture();
        fixture.Settings.GameCapabilities[Game] = new GameCapabilities
        {
            WheelSpeedMode = GameCapabilities.ModeMono,
            ABSMode = GameCapabilities.Available,
        };
        fixture.Frame(vs: 30.0, sway: 0.0, brake: 0.0);
        Assert.Equal(DetectionState.Mono, fixture.Detector.State);

        SetWheelSpeeds(fixture.Reader, 30.0, 30.0, 30.0, 30.0);
        fixture.Detector.RequestRetest();
        Assert.Equal(DetectionState.Mono, fixture.Detector.State, "applied on the next frame");
        fixture.Frame(vs: 0.0, sway: 0.0, brake: 0.0);

        Assert.Equal(DetectionState.Detecting, fixture.Detector.State);
        Assert.False(fixture.Settings.GameCapabilities.ContainsKey(Game), "entry removed");
        Assert.True(fixture.Detector.SettingsChanged);
        Assert.True(fixture.Capabilities.AbsEverActive, "in-memory flag kept (v1)");

        SetWheelSpeeds(fixture.Reader, 30.0, 31.0, 30.0, 30.0);
        fixture.Frame(vs: 30.0, sway: 1.0, brake: 0.0);
        Assert.Equal(DetectionState.PerWheel, fixture.Detector.State);

        // The recreated entry receives the ever-active flag on the next capability update (v1 PersistDetection).
        fixture.Frame(vs: 30.0, sway: 1.0, brake: 0.0);
        Assert.Equal(GameCapabilities.Available, fixture.Settings.GameCapabilities[Game].ABSMode);
    }

    [Test]
    public void Detector_WithoutGameNameDecidesButDoesNotPersist()
    {
        var fixture = new DetectorFixture(string.Empty);
        fixture.Frame(vs: 30.0, sway: 0.0, brake: 0.0);
        Assert.Equal(DetectionState.Mono, fixture.Detector.State);
        Assert.Equal(0, fixture.Settings.GameCapabilities.Count);
        Assert.False(fixture.Detector.SettingsChanged);
    }

    // ------------------------------------------------------------------ CapabilityTracker

    [Test]
    public void Capabilities_TriStatesLevelsAndExportedFlags()
    {
        var settings = new HapticsSettings();
        var tracker = new CapabilityTracker(settings);
        tracker.Reset(Game);
        var reader = new FakeTelemetryReader();

        tracker.Update(reader, absActive: false, tcActive: false);
        Assert.False(tracker.GameExportsAbs);
        Assert.False(tracker.GameExportsTc);
        Assert.Near(CapabilityTracker.LevelNotExported, tracker.AbsLevel, 0.0);
        Assert.Equal(TriState.No, tracker.CarHasAbs, "not exported -> No");
        Assert.Equal(TriState.No, tracker.CarHasTc);
        Assert.False(tracker.AbsEnabled);

        reader.Set(PropertyPaths.AbsLevel, 0).Set(PropertyPaths.TcLevel, 3);
        tracker.Update(reader, absActive: false, tcActive: false);
        Assert.True(tracker.GameExportsAbs);
        Assert.True(tracker.GameExportsTc);
        Assert.Equal(TriState.Unknown, tracker.CarHasAbs, "exported but never active -> Unknown");
        Assert.False(tracker.AbsEnabled, "level 0 = off");
        Assert.True(tracker.TcEnabled, "level 3 = on");
        Assert.Near(3.0, tracker.TcLevel, 0.0);

        tracker.Update(reader, absActive: true, tcActive: false);
        Assert.Equal(TriState.Yes, tracker.CarHasAbs, "seen active -> Yes");
        Assert.Equal(TriState.Unknown, tracker.CarHasTc);

        tracker.Update(reader, absActive: false, tcActive: false);
        Assert.Equal(TriState.Yes, tracker.CarHasAbs, "ever flag is sticky");

        reader.Clear();
        tracker.Update(reader, absActive: false, tcActive: false);
        Assert.Equal(TriState.Yes, tracker.CarHasAbs, "ever active beats not exported");
        Assert.Equal(TriState.No, tracker.CarHasTc);

        tracker.Reset(Game);
        Assert.False(tracker.AbsEverActive, "game change forgets the flags");
    }

    [Test]
    public void Capabilities_PersistOnlyIntoExistingEntry()
    {
        var settings = new HapticsSettings();
        var tracker = new CapabilityTracker(settings);
        tracker.Reset(Game);
        var reader = new FakeTelemetryReader();

        tracker.Update(reader, absActive: true, tcActive: true);
        Assert.False(settings.GameCapabilities.ContainsKey(Game), "v1 never creates the entry here");
        Assert.False(tracker.SettingsChanged);

        settings.GameCapabilities[Game] = new GameCapabilities { WheelSpeedMode = GameCapabilities.ModeMono };
        tracker.Update(reader, absActive: false, tcActive: false);
        Assert.Equal(GameCapabilities.Available, settings.GameCapabilities[Game].ABSMode);
        Assert.Equal(GameCapabilities.Available, settings.GameCapabilities[Game].TCMode);
        Assert.True(tracker.SettingsChanged);

        tracker.ClearSettingsChanged();
        tracker.Update(reader, absActive: true, tcActive: true);
        Assert.False(tracker.SettingsChanged, "already persisted -> no change");
    }

    // ------------------------------------------------------------------ CarIdentityResolver

    [Test]
    public void CarIdentity_LmuPrefersNativeModelBytes()
    {
        var ctx = Context("LMU", carId: "GT3_Iron Lynx 2026_61", carModel: "296GT3 Custom Team 2025", carClass: "GT3");
        var bytes = new byte[30];
        Encoding.ASCII.GetBytes(" Ligier JS P325 ").CopyTo(bytes, 0);
        var reader = new FakeTelemetryReader()
            .Set(PropertyPaths.LmuVehicleModel, bytes)
            .Set(PropertyPaths.LmuVehicleClass, FakeVehicleClass.LMP3);

        CarIdentity identity = CarIdentityResolver.Resolve(ctx, reader);
        Assert.Equal("LMU", identity.SimKey);
        Assert.Equal("Ligier JS P325", identity.CarKey, "NUL-terminated and trimmed");
        Assert.Equal("Ligier JS P325", identity.DisplayName);
        Assert.Equal("LMP3", identity.CarClass, "native class wins");
        Assert.Equal(CarKeySource.NativeModel, identity.KeySource);
        Assert.True(identity.HasCar);
        Assert.False(identity.ShouldRetry);
    }

    [Test]
    public void CarIdentity_LmuEmptyModelFallsBackAndAsksForRetry()
    {
        var ctx = Context("LMU", carId: "GT3_Iron Lynx 2026_61", carModel: "296GT3 Custom Team 2025", carClass: "GT3");
        var reader = new FakeTelemetryReader()
            .Set(PropertyPaths.LmuVehicleModel, new byte[30])
            .Set(PropertyPaths.LmuVehicleClass, "Unknown");

        CarIdentity identity = CarIdentityResolver.Resolve(ctx, reader);
        Assert.Equal("296GT3 Custom Team 2025", identity.CarKey);
        Assert.Equal(CarKeySource.CarModel, identity.KeySource);
        Assert.Equal("GT3", identity.CarClass, "'Unknown' native class ignored");
        Assert.True(identity.ShouldRetry, "livery-specific fallback on LMU");

        reader.Set(PropertyPaths.LmuVehicleModel, "N/A").Set(PropertyPaths.LmuVehicleClass, 3);
        ctx.CarModel = "N/A";
        identity = CarIdentityResolver.Resolve(ctx, reader);
        Assert.Equal("GT3_Iron Lynx 2026_61", identity.CarKey);
        Assert.Equal(CarKeySource.CarId, identity.KeySource);
        Assert.Equal("GT3", identity.CarClass, "numeric enum value ignored");
        Assert.True(identity.ShouldRetry);

        reader.Set(PropertyPaths.LmuVehicleModel, Encoding.ASCII.GetBytes("Oreca 07\0garbage"));
        identity = CarIdentityResolver.Resolve(ctx, reader);
        Assert.Equal("Oreca 07", identity.CarKey, "text stops at NUL");
        Assert.False(identity.ShouldRetry);
    }

    [Test]
    public void CarIdentity_OtherSimsUseCarModelThenCarId()
    {
        var reader = new FakeTelemetryReader().Set(PropertyPaths.LmuVehicleModel, "must not be used");

        CarIdentity iracing = CarIdentityResolver.Resolve(Context("IRacing", "carid-1", " Mazda MX-5 Cup ", "MX5"), reader);
        Assert.Equal("Mazda MX-5 Cup", iracing.CarKey);
        Assert.Equal(CarKeySource.CarModel, iracing.KeySource);
        Assert.Equal("MX5", iracing.CarClass);
        Assert.False(iracing.ShouldRetry, "no native model expected outside rFactor sims");

        CarIdentity acc = CarIdentityResolver.Resolve(Context("AssettoCorsaCompetizione", "ferrari_296_gt3", "N/A", null), reader);
        Assert.Equal("ferrari_296_gt3", acc.CarKey);
        Assert.Equal(CarKeySource.CarId, acc.KeySource);
        Assert.Equal(string.Empty, acc.CarClass);

        Assert.True(iracing.SameCarAs(CarIdentityResolver.Resolve(Context("IRacing", "other-id", "Mazda MX-5 Cup", ""), reader)));
        Assert.False(iracing.SameCarAs(acc));
    }

    [Test]
    public void CarIdentity_EmptyOrNotAvailableEverywhereMeansNoCar()
    {
        var reader = new FakeTelemetryReader().Set(PropertyPaths.LmuVehicleModel, new byte[30]);

        CarIdentity none = CarIdentityResolver.Resolve(Context("LMU", "N/A", " ", "GT3"), reader);
        Assert.False(none.HasCar);
        Assert.Equal(CarKeySource.None, none.KeySource);
        Assert.Equal(string.Empty, none.CarKey);
        Assert.Equal("LMU", none.SimKey);
        Assert.False(none.ShouldRetry, "nothing to retry without any car");

        Assert.False(CarIdentityResolver.Resolve(Context("IRacing", null, null, null), new FakeTelemetryReader()).HasCar);
        Assert.False(CarIdentity.None.HasCar);
    }

    // ------------------------------------------------------------------ helpers

    private static void SetAll(FakeTelemetryReader reader, string[] paths, object value)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            reader.Set(paths[i], value);
        }
    }

    private static void SetWheelSpeeds(FakeTelemetryReader reader, double fl, double fr, double rl, double rr)
    {
        reader.Set(PropertyPaths.IRacingWheelSpeed[0], fl)
            .Set(PropertyPaths.IRacingWheelSpeed[1], fr)
            .Set(PropertyPaths.IRacingWheelSpeed[2], rl)
            .Set(PropertyPaths.IRacingWheelSpeed[3], rr);
    }

    private static FrameContext Context(string game, string carId, string carModel, string carClass) => new FrameContext
    {
        GameRunning = true,
        GameName = game,
        CarId = carId,
        CarModel = carModel,
        CarClass = carClass,
    };

    /// <summary>Wires resolver, capability tracker and detector in the shell's per-frame order (DESIGN 3.1 steps 7-8).</summary>
    private sealed class DetectorFixture
    {
        private double wallTime;

        public DetectorFixture(string game = Game)
        {
            Settings = new HapticsSettings();
            Resolver = new SlipSourceResolver();
            Capabilities = new CapabilityTracker(Settings);
            Detector = new WheelSpeedModeDetector(Settings, Resolver, Capabilities);
            ChangeGame(game);
        }

        public HapticsSettings Settings { get; }

        public SlipSourceResolver Resolver { get; }

        public CapabilityTracker Capabilities { get; }

        public WheelSpeedModeDetector Detector { get; }

        public FakeTelemetryReader Reader { get; } = new FakeTelemetryReader();

        public double[] BaseSlip { get; } = new double[Wheels.Count];

        public void ChangeGame(string game)
        {
            Resolver.Reset();
            Capabilities.Reset(game);
            Detector.Reset(game);
        }

        public void Frame(double vs, double sway, double brake)
        {
            wallTime += 1.0 / 60.0;
            Capabilities.Update(Reader, absActive: false, tcActive: false);
            Resolver.Probe(Reader, wallTime);
            Detector.ComputeBaseSlip(Reader, vs, sway, brake, wallTime, BaseSlip);
        }
    }
}
