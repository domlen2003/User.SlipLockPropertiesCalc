using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using User.SlipLockPropertiesCalc.Balance;
using User.SlipLockPropertiesCalc.Balance.Recording;
using User.SlipLockPropertiesCalc.Balance.Sources;
using User.SlipLockPropertiesCalc.Core;
using User.SlipLockPropertiesCalc.Telemetry;

namespace User.SlipLockPropertiesCalc.Tests;

/// <summary>Vehicle-state adapters, factory, CSV format, recorder and replay tool.</summary>
internal sealed class SourceTests
{
    /// <summary>Most fake values are floats (like the sims' shared memory), so compare at float precision.</summary>
    private const double Tolerance = 1e-6;

    private const string IR = "DataCorePlugin.GameRawData.Telemetry.";
    private const string RT = "DataCorePlugin.GameRawData.CurrentPlayerTelemetry.";
    private const string RS = "DataCorePlugin.GameRawData.CurrentPlayer.";
    private const string AP = "DataCorePlugin.GameRawData.Physics.";

    // ------------------------------------------------------------------ iRacing

    [Test]
    public void IRacing_MapsFieldsUnitsAndSigns()
    {
        FakeTelemetryReader reader = IRacingReader();
        var source = new IRacingStateSource();
        var state = new VehicleState();
        FrameContext ctx = Ctx(10.0);
        ctx.Throttle = 40;
        ctx.Brake = 150;
        ctx.Gear = "3";
        ctx.GearNumber = 3;

        Assert.True(source.Read(ctx, reader, state), "valid");
        Assert.Near(123.5, state.SimTime, Tolerance, "SimTime");
        Assert.Near(10.0, state.WallTime, Tolerance, "WallTime");
        Assert.Near(40.0, state.V, Tolerance, "V");
        Assert.Near(-1.5, state.Vy, Tolerance, "Vy");
        Assert.Near(0.2, state.Theta, Tolerance, "Theta");
        Assert.Near(4.0, state.ThetaMax, Tolerance, "ThetaMax");
        Assert.Near(0.3, state.R, Tolerance, "R");
        Assert.Near(12.0, state.Ay, Tolerance, "Ay");
        Assert.Near(-3.0, state.Ax, Tolerance, "Ax");
        Assert.Near(9.8, state.Az, Tolerance, "Az");
        Assert.Near(0.4, state.Throttle, Tolerance, "Throttle from ctx");
        Assert.Near(1.0, state.Brake, Tolerance, "Brake clamped");
        Assert.Equal(3, state.Gear, "Gear from ctx");
        Assert.True(state.OnTrack, "OnTrack");
        Assert.False(state.OnPitRoad, "OnPitRoad");
        Assert.False(state.InGarage, "InGarage");
        Assert.False(state.IsReplay, "IsReplay");
        Assert.Equal(SurfaceKind.Unknown, state.Surface, "Surface");
        Assert.False(state.HasSlipAngles, "no slip angles");
        Assert.Equal("iRacing", source.Name);
        Assert.True(source.IsSupported);
    }

    [Test]
    public void IRacing_ThetaMaxBelowPlausibleIsUnknown()
    {
        FakeTelemetryReader reader = IRacingReader().Set(IR + "SteeringWheelAngleMax", 0.05f);
        var state = new VehicleState();
        new IRacingStateSource().Read(Ctx(0), reader, state);
        Assert.True(double.IsNaN(state.ThetaMax), "ThetaMax NaN");
    }

    [Test]
    public void IRacing_FlagsFromRawAndContext()
    {
        FakeTelemetryReader reader = IRacingReader()
            .Set(IR + "OnPitRoad", true)
            .Set(IR + "IsInGarage", true)
            .Set(IR + "IsReplayPlaying", true);
        var source = new IRacingStateSource();
        var state = new VehicleState();
        source.Read(Ctx(0), reader, state);
        Assert.True(state.OnPitRoad, "raw OnPitRoad");
        Assert.True(state.InGarage, "raw InGarage");
        Assert.True(state.IsReplay, "raw IsReplayPlaying");

        reader = IRacingReader();
        FrameContext ctx = Ctx(0);
        ctx.IsInPitLane = true;
        ctx.IsReplay = true;
        ctx.Spectating = true;
        ctx.GameInMenu = true;
        new IRacingStateSource().Read(ctx, reader, state);
        Assert.True(state.OnPitRoad, "ctx pit lane");
        Assert.True(state.IsReplay, "ctx replay");
        Assert.True(state.IsSpectating, "ctx spectating");
        Assert.True(state.IsPaused, "menu = paused");

        ctx = Ctx(0);
        ctx.GamePaused = true;
        new IRacingStateSource().Read(ctx, reader, state);
        Assert.True(state.IsPaused, "paused");
    }

    [Test]
    public void IRacing_OnTrackFallbacks()
    {
        var state = new VehicleState();

        FakeTelemetryReader reader = IRacingReader().Set(IR + "IsOnTrack", false).Set(IR + "IsOnTrackCar", true);
        new IRacingStateSource().Read(Ctx(0), reader, state);
        Assert.False(state.OnTrack, "IsOnTrack preferred");

        reader = IRacingReader().Remove(IR + "IsOnTrack").Set(IR + "IsOnTrackCar", false);
        new IRacingStateSource().Read(Ctx(0), reader, state);
        Assert.False(state.OnTrack, "IsOnTrackCar fallback");

        reader = IRacingReader().Remove(IR + "IsOnTrack").Set(IR + "IsOnTrackCar", true);
        new IRacingStateSource().Read(Ctx(0), reader, state);
        Assert.True(state.OnTrack, "IsOnTrackCar fallback true");

        reader = IRacingReader().Remove(IR + "IsOnTrack");
        new IRacingStateSource().Read(Ctx(0), reader, state);
        Assert.True(state.OnTrack, "missing = on track");
    }

    [Test]
    public void IRacing_WorldFrameVelocityFallsBackToSpeed()
    {
        FakeTelemetryReader reader = IRacingReader().Set(IR + "Speed", 50.0f).Set(IR + "VelocityX", 15.0f).Set(IR + "VelocityY", 47.0f);
        var source = new IRacingStateSource();
        var state = new VehicleState();

        for (int i = 0; i < 59; i++)
        {
            source.Read(Ctx(i * 0.016), reader, state);
            Assert.Near(15.0, state.V, Tolerance, "VelocityX used while checking");
        }

        Assert.False(source.UsesSpeedFallback, "still pending");
        source.Read(Ctx(1.0), reader, state);
        Assert.True(source.UsesSpeedFallback, "decided after 60 fast samples");
        Assert.Near(50.0, state.V, Tolerance, "V = Speed");
        Assert.True(double.IsNaN(state.Vy), "Vy unknown");

        // The decision sticks even if VelocityX later looks plausible.
        reader.Set(IR + "VelocityX", 50.0f);
        source.Read(Ctx(1.1), reader, state);
        Assert.Near(50.0, state.V, Tolerance, "still Speed");
        Assert.True(state.Valid, "valid on Speed");

        source.Reset();
        source.Read(Ctx(2.0), reader, state);
        Assert.False(source.UsesSpeedFallback, "reset re-checks");
        Assert.Near(50.0, state.V, Tolerance, "VelocityX again");
    }

    [Test]
    public void IRacing_CarFrameVelocityKeepsVelocityX()
    {
        FakeTelemetryReader reader = IRacingReader().Set(IR + "Speed", 50.0f).Set(IR + "VelocityX", 49.5f);
        var source = new IRacingStateSource();
        var state = new VehicleState();

        // Slow frames do not count toward the check.
        reader.Set(IR + "Speed", 10.0f);
        for (int i = 0; i < 100; i++)
        {
            source.Read(Ctx(i * 0.016), reader, state);
        }

        reader.Set(IR + "Speed", 50.0f);
        for (int i = 0; i < 100; i++)
        {
            source.Read(Ctx(2 + (i * 0.016)), reader, state);
        }

        Assert.False(source.UsesSpeedFallback, "car frame");
        Assert.Near(49.5, state.V, 1e-5, "V = VelocityX");
        Assert.Near(-1.5, state.Vy, Tolerance, "Vy kept");
        Assert.True(source.DescribeResolution().Contains("V = VelocityX"), "diagnostics show the decision");
    }

    [Test]
    public void IRacing_MissingVelocityXUsesSpeed()
    {
        FakeTelemetryReader reader = IRacingReader().Remove(IR + "VelocityX").Set(IR + "Speed", 33.0f);
        var state = new VehicleState();
        Assert.True(new IRacingStateSource().Read(Ctx(0), reader, state), "valid");
        Assert.Near(33.0, state.V, Tolerance, "V from Speed");
        Assert.True(double.IsNaN(state.Vy), "no lateral velocity");
    }

    // ------------------------------------------------------------------ rFactor2 / LMU

    [Test]
    public void RFactor_MapsAxesAndSigns()
    {
        FakeTelemetryReader reader = RFactorReader();
        var source = new RFactorStateSource();
        var state = new VehicleState();

        Assert.True(source.Read(Ctx(5.0), reader, state), "valid");
        Assert.Near(812.25, state.SimTime, Tolerance, "SimTime = mElapsedTime");
        Assert.Near(45.0, state.V, Tolerance, "V = -z");
        Assert.Near(0.8, state.Vy, 1e-6, "Vy = x");
        Assert.Near(0.25, state.R, Tolerance, "R = rot.y");
        Assert.Near(11.0, state.Ay, Tolerance, "Ay = accel.x");
        Assert.Near(-4.0, state.Ax, Tolerance, "Ax = -accel.z");
        Assert.Near(9.81, state.Az, 1e-6, "Az = accel.y");
        Assert.Equal("rFactor2/LMU", source.Name);
    }

    [Test]
    public void RFactor_SteeringScalesWithRangeAndFallbacks()
    {
        FakeTelemetryReader reader = RFactorReader().Set(RT + "mFilteredSteering", 0.5).Set(RT + "mPhysicalSteeringWheelRange", 360.0f).Set(RT + "mVisualSteeringWheelRange", 900.0f);
        var state = new VehicleState();

        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.Near(180.0 * MathUtil.DegToRad, state.ThetaMax, 1e-9, "half of physical range");
        Assert.Near(-0.5 * 180.0 * MathUtil.DegToRad, state.Theta, 1e-9, "right steer = negative theta");

        reader.Set(RT + "mPhysicalSteeringWheelRange", 0.0f);
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.Near(450.0 * MathUtil.DegToRad, state.ThetaMax, 1e-9, "visual fallback");

        reader.Remove(RT + "mPhysicalSteeringWheelRange").Set(RT + "mVisualSteeringWheelRange", 0.0f);
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.Near(270.0 * MathUtil.DegToRad, state.ThetaMax, 1e-9, "540 default");
        Assert.Near(-0.5 * 270.0 * MathUtil.DegToRad, state.Theta, 1e-9, "theta with default range");

        reader.Remove(RT + "mFilteredSteering");
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.True(double.IsNaN(state.Theta), "missing steering = NaN");
    }

    [Test]
    public void RFactor_PedalsAndGearFromRawWithContextFallback()
    {
        FakeTelemetryReader reader = RFactorReader().Set(RT + "mFilteredThrottle", 0.75).Set(RT + "mFilteredBrake", 1.2).Set(RT + "mGear", -1);
        FrameContext ctx = Ctx(0);
        ctx.Throttle = 10;
        ctx.Brake = 20;
        ctx.GearNumber = 4;
        var state = new VehicleState();

        new RFactorStateSource().Read(ctx, reader, state);
        Assert.Near(0.75, state.Throttle, Tolerance, "raw throttle");
        Assert.Near(1.0, state.Brake, Tolerance, "raw brake clamped");
        Assert.Equal(-1, state.Gear, "raw gear");

        reader.Remove(RT + "mFilteredThrottle").Remove(RT + "mFilteredBrake").Remove(RT + "mGear");
        new RFactorStateSource().Read(ctx, reader, state);
        Assert.Near(0.1, state.Throttle, Tolerance, "ctx throttle");
        Assert.Near(0.2, state.Brake, Tolerance, "ctx brake");
        Assert.Equal(4, state.Gear, "ctx gear");
    }

    [Test]
    public void RFactor_ControlGarageAndPits()
    {
        var state = new VehicleState();

        FakeTelemetryReader reader = RFactorReader();
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.True(state.OnTrack, "player (0) on track");
        Assert.False(state.IsReplay, "no replay");

        reader = RFactorReader().Remove(RS + "mControl");
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.True(state.OnTrack, "missing control = player");

        reader = RFactorReader().Set(RS + "mInGarageStall", (byte)1);
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.True(state.InGarage, "garage");
        Assert.False(state.OnTrack, "garage = not on track");

        reader = RFactorReader().Set(RS + "mInPits", (byte)1);
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.True(state.OnPitRoad, "pits");
        Assert.True(state.OnTrack, "pit lane still on track");

        reader = RFactorReader().Set(RS + "mControl", (sbyte)3);
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.True(state.IsReplay, "replay control");

        foreach (sbyte control in new sbyte[] { -1, 1, 2 })
        {
            reader = RFactorReader().Set(RS + "mControl", control);
            new RFactorStateSource().Read(Ctx(0), reader, state);
            Assert.False(state.OnTrack, "not player-controlled: " + control);
            Assert.False(state.IsReplay, "not a replay: " + control);
        }
    }

    [Test]
    public void RFactor_SurfaceClassification()
    {
        Assert.Equal(SurfaceKind.Asphalt, Surface(0, 0, 0, 0), "dry");
        Assert.Equal(SurfaceKind.Asphalt, Surface(2, 0, 0, 0), "one wheel on grass");
        Assert.Equal(SurfaceKind.Loose, Surface(2, 3, 0, 0), "two wheels grass/dirt");
        Assert.Equal(SurfaceKind.Loose, Surface(4, 4, 5, 0), "loose beats kerb");
        Assert.Equal(SurfaceKind.Kerb, Surface(5, 0, 0, 0), "rumble strip");
        Assert.Equal(SurfaceKind.Kerb, Surface(5, 1, 1, 1), "kerb beats wet");
        Assert.Equal(SurfaceKind.Wet, Surface(1, 1, 1, 0), "three wet");
        Assert.Equal(SurfaceKind.Asphalt, Surface(1, 1, 0, 0), "two wet");
        Assert.Equal(SurfaceKind.Asphalt, Surface(6, 0, 0, 0), "special");

        FakeTelemetryReader reader = RFactorReader();
        for (int i = 0; i < Wheels.Count; i++)
        {
            reader.Remove(RT + "mWheels" + Wheels.RawSuffixes[i] + ".mSurfaceType");
        }

        var state = new VehicleState();
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.Equal(SurfaceKind.Unknown, state.Surface, "no surface data");
    }

    [Test]
    public void RFactor_WetnessOptional()
    {
        var state = new VehicleState();
        new RFactorStateSource().Read(Ctx(0), RFactorReader(), state);
        Assert.True(double.IsNaN(state.Wetness), "missing wetness = NaN");

        FakeTelemetryReader reader = RFactorReader().Set("DataCorePlugin.GameRawData.Data.mAvgPathWetness", 0.4);
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.Near(0.4, state.Wetness, Tolerance, "wetness");
    }

    [Test]
    public void RFactor_ContactCounterOnImpactTimeChange()
    {
        FakeTelemetryReader reader = RFactorReader().Set(RT + "mLastImpactET", 100.0);
        var source = new RFactorStateSource();
        var state = new VehicleState();

        source.Read(Ctx(0), reader, state);
        Assert.Equal(0, state.ContactCounter, "first value is a baseline");
        source.Read(Ctx(0.01), reader, state);
        Assert.Equal(0, state.ContactCounter, "unchanged");

        reader.Set(RT + "mLastImpactET", 812.0);
        source.Read(Ctx(0.02), reader, state);
        Assert.Equal(1, state.ContactCounter, "new impact");
        source.Read(Ctx(0.03), reader, state);
        Assert.Equal(1, state.ContactCounter, "same impact");

        reader.Set(RT + "mLastImpactET", 815.5);
        source.Read(Ctx(0.04), reader, state);
        Assert.Equal(2, state.ContactCounter, "second impact");

        reader.Set(RT + "mLastImpactET", 0.0);
        source.Read(Ctx(0.05), reader, state);
        Assert.Equal(2, state.ContactCounter, "reset to 0 (session restart) is not a contact");
    }

    [Test]
    public void RFactor_SlipAngles()
    {
        FakeTelemetryReader reader = RFactorReader();
        SetWheel(reader, 0, lateralPatch: 2.0, longitudinalGround: -40.0);
        SetWheel(reader, 1, lateralPatch: -1.0, longitudinalGround: -40.0);
        SetWheel(reader, 2, lateralPatch: 4.0, longitudinalGround: -40.0);
        SetWheel(reader, 3, lateralPatch: 3.0, longitudinalGround: -40.0);
        var state = new VehicleState();

        new RFactorStateSource().Read(Ctx(0), reader, state);
        double a0 = Math.Atan2(2.0, 40.0);
        double a1 = Math.Atan2(-1.0, 40.0);
        double a2 = Math.Atan2(4.0, 40.0);
        double a3 = Math.Atan2(3.0, 40.0);
        Assert.True(state.HasSlipAngles, "has slip angles");
        Assert.True(state.SlipAnglesInRadians, "radians");
        Assert.Near(a0, state.Alpha[0], 1e-12, "alpha FL signed");
        Assert.Near(a1, state.Alpha[1], 1e-12, "alpha FR signed");
        Assert.Near((Math.Abs(a0) + Math.Abs(a1)) / 2, state.AlphaFront, 1e-12, "front mean |alpha|");
        Assert.Near((Math.Abs(a2) + Math.Abs(a3)) / 2, state.AlphaRear, 1e-12, "rear mean |alpha|");

        // A wheel with too little ground speed has no slip angle → no direct path.
        SetWheel(reader, 3, lateralPatch: 3.0, longitudinalGround: 0.5);
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.False(state.HasSlipAngles, "one wheel invalid");
        Assert.True(double.IsNaN(state.Alpha[3]), "alpha RR NaN");
        Assert.True(double.IsNaN(state.AlphaRear), "rear mean unknown");
        Assert.True(MathUtil.IsFinite(state.AlphaFront), "front mean still known");

        // Too slow overall.
        SetWheel(reader, 3, lateralPatch: 3.0, longitudinalGround: -40.0);
        reader.Set(RT + "mLocalVel.z", 4.0);
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.False(state.HasSlipAngles, "V <= 5 m/s");

        // Missing patch velocity.
        reader = RFactorReader().Remove(RT + "mWheels02.mLateralPatchVel");
        new RFactorStateSource().Read(Ctx(0), reader, state);
        Assert.False(state.HasSlipAngles, "missing wheel data");
    }

    // ------------------------------------------------------------------ Assetto Corsa family

    [Test]
    public void Acc_PascalCaseMapping()
    {
        FakeTelemetryReader reader = AccReader(camel: false, slipAngles: true);
        FrameContext ctx = Ctx(3.0);
        ctx.Throttle = 55;
        ctx.GearNumber = 2;
        var source = new AccStateSource();
        var state = new VehicleState();

        Assert.True(source.Read(ctx, reader, state), "valid");
        AssertAccValues(state);
        Assert.True(double.IsNaN(state.SimTime), "no sim time");
        Assert.Near(3.0, state.WallTime, Tolerance, "wall time");
        Assert.Near(0.55, state.Throttle, Tolerance, "ctx throttle");
        Assert.Equal(2, state.Gear, "ctx gear");
        Assert.True(state.OnTrack, "not in pit = on track");
        Assert.Equal(SurfaceKind.Unknown, state.Surface, "surface");
        Assert.True(state.HasSlipAngles, "ACC slip angles");
        Assert.False(state.SlipAnglesInRadians, "unit unknown");
        Assert.Near((0.04 + 0.06) / 2, state.AlphaFront, 1e-7, "front mean |sa|");
        Assert.Near((0.02 + 0.03) / 2, state.AlphaRear, 1e-7, "rear mean |sa|");
        Assert.Near(-0.06, state.Alpha[1], 1e-7, "signed per wheel");
        Assert.Equal("ACC/AC", source.Name);
    }

    [Test]
    public void Acc_CamelCaseEvoMapping()
    {
        FakeTelemetryReader reader = AccReader(camel: true, slipAngles: true);
        var state = new VehicleState();
        var source = new AccStateSource();
        Assert.True(source.Read(Ctx(0), reader, state), "valid");
        AssertAccValues(state);
        Assert.True(source.DescribeResolution().Contains(AP + "localAngularVel02"), "camel path resolved");
    }

    [Test]
    public void Acc_WithoutSlipAngles_OriginalAc()
    {
        FakeTelemetryReader reader = AccReader(camel: false, slipAngles: false);
        var state = new VehicleState();
        Assert.True(new AccStateSource().Read(Ctx(0), reader, state), "valid on model path");
        Assert.False(state.HasSlipAngles, "AC has no slip angles");
        Assert.True(double.IsNaN(state.AlphaFront), "front unknown");

        reader.Set(AP + "slipAngle01", 0.1f).Set(AP + "slipAngle02", 0.1f).Set(AP + "slipAngle03", 0.1f);
        new AccStateSource().Read(Ctx(0), reader, state);
        Assert.False(state.HasSlipAngles, "all four required");
    }

    [Test]
    public void Acc_InPitIsNotOnTrack()
    {
        FrameContext ctx = Ctx(0);
        ctx.IsInPit = true;
        var state = new VehicleState();
        new AccStateSource().Read(ctx, AccReader(false, false), state);
        Assert.False(state.OnTrack, "in pit");
    }

    [Test]
    public void Acc_ProbingIsRateLimitedAndResetReResolves()
    {
        FakeTelemetryReader reader = AccReader(camel: false, slipAngles: false);
        var source = new AccStateSource();
        var state = new VehicleState();
        source.Read(Ctx(10.0), reader, state);
        Assert.False(state.HasSlipAngles, "none yet");

        reader.Set(AP + "slipAngle01", 0.1f).Set(AP + "slipAngle02", 0.1f).Set(AP + "slipAngle03", 0.1f).Set(AP + "slipAngle04", 0.1f);
        source.Read(Ctx(10.5), reader, state);
        Assert.False(state.HasSlipAngles, "not probed again within 1 s");
        source.Read(Ctx(11.0), reader, state);
        Assert.True(state.HasSlipAngles, "probed after 1 s");

        // Reset forgets the PascalCase paths so a camelCase-only variant resolves.
        FakeTelemetryReader camelReader = AccReader(camel: true, slipAngles: false);
        source.Read(Ctx(11.1), camelReader, state);
        Assert.False(state.Valid, "stale Pascal paths before reset");
        source.Reset();
        source.Read(Ctx(11.2), camelReader, state);
        Assert.True(state.Valid, "camel paths after reset");
        AssertAccValues(state);
    }

    [Test]
    public void Acc_FewLookupsOnceResolved()
    {
        FakeTelemetryReader reader = AccReader(camel: true, slipAngles: false);
        var source = new AccStateSource();
        var state = new VehicleState();
        source.Read(Ctx(0), reader, state);

        int before = reader.ReadCount;
        source.Read(Ctx(0.1), reader, state);
        Assert.Equal(7, reader.ReadCount - before, "one lookup per resolved field, none for unresolved ones between probes");
    }

    // ------------------------------------------------------------------ Factory, null source, validity

    [Test]
    public void Factory_MapsGameNamesCaseInsensitive()
    {
        Assert.True(VehicleStateSourceFactory.Create("IRacing") is IRacingStateSource, "IRacing");
        Assert.True(VehicleStateSourceFactory.Create("iracing") is IRacingStateSource, "iracing");
        Assert.True(VehicleStateSourceFactory.Create("LMU") is RFactorStateSource, "LMU");
        Assert.True(VehicleStateSourceFactory.Create("lmu") is RFactorStateSource, "lmu");
        Assert.True(VehicleStateSourceFactory.Create("RFactor2") is RFactorStateSource, "RFactor2");
        Assert.True(VehicleStateSourceFactory.Create("AssettoCorsaCompetizione") is AccStateSource, "ACC");
        Assert.True(VehicleStateSourceFactory.Create("assettocorsa") is AccStateSource, "AC");
        Assert.True(VehicleStateSourceFactory.Create("AssettoCorsaEVO") is AccStateSource, "EVO");
        Assert.True(VehicleStateSourceFactory.Create("AssettoCorsaRally") is AccStateSource, "Rally");
        Assert.True(VehicleStateSourceFactory.Create("BeamNgDrive") is NullStateSource, "unknown");
        Assert.True(VehicleStateSourceFactory.Create(string.Empty) is NullStateSource, "empty");
        Assert.True(VehicleStateSourceFactory.Create(null) is NullStateSource, "null");
        Assert.Equal(VehicleStateSourceKind.RFactor, VehicleStateSourceFactory.GetKind("LMU"));
        Assert.Equal(VehicleStateSourceKind.None, VehicleStateSourceFactory.GetKind("F1 2024"));
    }

    [Test]
    public void NullSource_IsUnsupportedAndInvalid()
    {
        IVehicleStateSource source = VehicleStateSourceFactory.Create("BeamNgDrive");
        var state = new VehicleState { V = 10, R = 1 };
        FrameContext ctx = Ctx(7.0);
        ctx.Throttle = 50;
        Assert.False(source.IsSupported, "unsupported");
        Assert.False(source.Read(ctx, new FakeTelemetryReader(), state), "invalid");
        Assert.False(state.Valid, "state invalid");
        Assert.True(double.IsNaN(state.V), "cleared");
        Assert.Near(7.0, state.WallTime, Tolerance, "wall time");
        Assert.Near(0.5, state.Throttle, Tolerance, "common fields still filled");
        Assert.True(source.DescribeResolution().Contains("BeamNgDrive"), "diagnostics name the game");
    }

    [Test]
    public void Validity_RequiresSpeedAndALateralSignal()
    {
        var state = new VehicleState();

        FakeTelemetryReader reader = AccReader(camel: false, slipAngles: false).Remove(AP + "LocalVelocity03");
        Assert.False(new AccStateSource().Read(Ctx(0), reader, state), "no V");

        reader = AccReader(camel: false, slipAngles: false).Remove(AP + "LocalAngularVelocity02").Remove(AP + "AccG01");
        Assert.False(new AccStateSource().Read(Ctx(0), reader, state), "V only");

        reader = AccReader(camel: false, slipAngles: false).Remove(AP + "LocalAngularVelocity02");
        Assert.True(new AccStateSource().Read(Ctx(0), reader, state), "V + Ay");

        reader = AccReader(camel: false, slipAngles: false).Remove(AP + "AccG01");
        Assert.True(new AccStateSource().Read(Ctx(0), reader, state), "V + R");

        reader = AccReader(camel: false, slipAngles: true).Remove(AP + "LocalAngularVelocity02").Remove(AP + "AccG01");
        Assert.True(new AccStateSource().Read(Ctx(0), reader, state), "V + slip angles");

        Assert.False(new IRacingStateSource().Read(Ctx(0), new FakeTelemetryReader(), state), "empty iRacing");
        Assert.False(new RFactorStateSource().Read(Ctx(0), new FakeTelemetryReader(), state), "empty rF2");
    }

    [Test]
    public void Read_PreservesContactCounterAndOverwritesStaleValues()
    {
        var state = new VehicleState { ContactCounter = 5, Vy = 99, Wetness = 0.9, HasSlipAngles = true };
        new IRacingStateSource().Read(Ctx(0), IRacingReader().Remove(IR + "VelocityY"), state);
        Assert.Equal(5, state.ContactCounter, "monotonic counter preserved");
        Assert.True(double.IsNaN(state.Vy), "stale Vy cleared");
        Assert.True(double.IsNaN(state.Wetness), "stale wetness cleared");
        Assert.False(state.HasSlipAngles, "stale slip flag cleared");
    }

    [Test]
    public void Read_IsAllocationFreeAfterWarmUp()
    {
        AssertNoAllocations("iRacing", new IRacingStateSource(), IRacingReader());
        AssertNoAllocations("rF2", new RFactorStateSource(), RFactorReader());
        AssertNoAllocations("ACC", new AccStateSource(), AccReader(camel: false, slipAngles: true));
        AssertNoAllocations("AC EVO", new AccStateSource(), AccReader(camel: true, slipAngles: false));
        AssertNoAllocations("null", new NullStateSource("X"), new FakeTelemetryReader());
    }

    [Test]
    public void Describe_ListsResolvedAndMissingPaths()
    {
        var source = new RFactorStateSource();
        source.Read(Ctx(0), RFactorReader(), new VehicleState());
        string text = source.DescribeResolution();
        Assert.True(text.Contains(RT + "mLocalVel.z"), "resolved path listed");
        Assert.True(text.Contains("Wetness: not found"), "missing field listed");
    }

    // ------------------------------------------------------------------ CSV and recorder

    [Test]
    public void Csv_RoundTripsEveryField()
    {
        var random = new Random(1234);
        var states = new List<VehicleState>();
        var outputs = new List<BalanceOutputs>();
        var writer = new StringWriter();
        BalanceCsv.WriteHeader(writer);
        for (int i = 0; i < 200; i++)
        {
            VehicleState s = RandomState(random, i);
            BalanceOutputs o = RandomOutputs(random);
            var record = default(BalanceRecord);
            record.CopyFrom(s, o);
            BalanceCsv.WriteRow(writer, ref record);
            states.Add(s);
            outputs.Add(o);
        }

        List<BalanceRecord> read = BalanceCsv.Read(new StringReader(writer.ToString())).ToList();
        Assert.Equal(states.Count, read.Count, "row count");
        var restored = new VehicleState();
        for (int i = 0; i < read.Count; i++)
        {
            read[i].ToVehicleState(restored);
            AssertStatesEqual(states[i], restored, "row " + i);
            Assert.True(read[i].Understeer.Equals(outputs[i].Understeer), "US row " + i);
            Assert.True(read[i].Oversteer.Equals(outputs[i].Oversteer), "OS row " + i);
            Assert.True(read[i].YawRatio.Equals(outputs[i].YawRatio), "rho row " + i);
            Assert.True(read[i].YawRef.Equals(outputs[i].YawRef), "rRef row " + i);
            Assert.True(read[i].BodySlipDeg.Equals(outputs[i].BodySlipDeg), "beta row " + i);
            Assert.Equal(outputs[i].Gate, read[i].Gate, "gate row " + i);
        }
    }

    [Test]
    public void Csv_HeaderStartsWithVehicleStateFieldsAndEndsWithOutputs()
    {
        string[] columns = BalanceCsv.Header.Split(',');
        Assert.Equal(BalanceCsv.ColumnCount, columns.Length, "column count");
        Assert.Equal("Valid", columns[0], "first column");
        Assert.True(columns.Contains("ContactCounter") && columns.Contains("AlphaRR"), "state fields");
        Assert.Equal("Gate", columns[columns.Length - 1], "last column");
        Assert.True(columns.Contains("Understeer") && columns.Contains("YawRatio") && columns.Contains("BodySlipDeg"), "outputs");
    }

    [Test]
    public void Csv_ToleratesReorderedColumnsTruncatedRowsAndComments()
    {
        string text = "# comment\n"
            + "V,valid,Surface,Extra,Gate\n"
            + "12.5,1,Loose,xyz,Contact\n"
            + "\n"
            + "7,0,Kerb\n"
            + "3.25,true,2,,Airborne\n";
        List<BalanceRecord> rows = BalanceCsv.Read(new StringReader(text)).ToList();
        Assert.Equal(2, rows.Count, "truncated row skipped");
        Assert.Near(12.5, rows[0].V, 0, "V");
        Assert.True(rows[0].Valid, "valid");
        Assert.Equal(SurfaceKind.Loose, rows[0].Surface, "surface by name");
        Assert.Equal(BalanceGate.Contact, rows[0].Gate, "gate");
        Assert.True(double.IsNaN(rows[0].R), "missing column = NaN");
        Assert.Equal(SurfaceKind.Kerb, rows[1].Surface, "surface by number (2 = Kerb)");
        Assert.True(rows[1].Valid, "'true' accepted");

        Assert.Throws<InvalidDataException>(() => BalanceCsv.Read(new StringReader("a,b\n1,2\n")).ToList(), "unknown header");
    }

    [Test]
    public void Ring_DropsOldestWhenFullAndDrainsInOrder()
    {
        var ring = new BalanceRecordRing(4);
        var state = new VehicleState();
        for (int i = 0; i < 6; i++)
        {
            state.V = i;
            ring.Add(state, null);
        }

        Assert.Equal(4, ring.Count, "full");
        Assert.Equal(2L, ring.Dropped, "two dropped");

        var batch = new BalanceRecord[3];
        Assert.Equal(3, ring.Drain(batch), "partial drain");
        Assert.Near(2, batch[0].V, 0, "oldest kept is #2");
        Assert.Near(4, batch[2].V, 0, "order");
        Assert.Equal(BalanceGate.NoData, batch[0].Gate, "null outputs → NoData");
        Assert.Equal(1, ring.Drain(batch), "remaining");
        Assert.Near(5, batch[0].V, 0, "newest");
        Assert.Equal(0, ring.Drain(batch), "empty");

        ring.Clear();
        Assert.Equal(0L, ring.Dropped, "clear resets drops");
    }

    [Test]
    public void Ring_AddIsAllocationFree()
    {
        var ring = new BalanceRecordRing(64);
        var state = new VehicleState { V = 30, R = 0.2 };
        var outputs = new BalanceOutputs();
        for (int i = 0; i < 1000; i++)
        {
            ring.Add(state, outputs);
        }

        long allocated = MeasureAllocations(() =>
        {
            for (int i = 0; i < 20000; i++)
            {
                ring.Add(state, outputs);
            }
        });
        Assert.True(allocated < AllocationSlackBytes, "ring allocated " + allocated + " bytes");
    }

    [Test]
    public void Recorder_BuildsSanitizedFileNames()
    {
        var time = new DateTime(2026, 9, 27, 14, 5, 9);
        Assert.Equal("LMU_Ligier_JS_P320_20260927_140509.csv", BalanceRecorder.BuildFileName("LMU", " Ligier JS P320 ", time));
        Assert.Equal("IRacing_a_b_c_20260927_140509.csv", BalanceRecorder.BuildFileName("IRacing", "a/b:c", time));
        Assert.Equal("unknown_unknown_20260927_140509.csv", BalanceRecorder.BuildFileName(null, "  ", time));
        string longName = BalanceRecorder.BuildFileName("S", new string('x', 200), time);
        Assert.True(longName.Length < 100, "length limited");
    }

    [Test]
    public void Recorder_WritesReadableCsv()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SlipLockSourceTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var recorder = new BalanceRecorder(NullLog.Instance, capacity: 1024, flushIntervalMs: 20))
            {
                var state = new VehicleState();
                var outputs = new BalanceOutputs();
                recorder.Record(state, outputs);
                Assert.False(recorder.IsRecording, "idle");

                string planned = recorder.Start(directory, "LMU", "Ligier JS P320");
                Assert.True(recorder.IsRecording, "recording");
                Assert.True(Path.GetFileName(planned).StartsWith("LMU_Ligier_JS_P320_", StringComparison.Ordinal), "file name");

                for (int i = 0; i < 50; i++)
                {
                    state.Valid = true;
                    state.SimTime = i * 0.01;
                    state.V = 20 + i;
                    state.Alpha[2] = -i;
                    outputs.Understeer = i / 100.0;
                    outputs.Gate = BalanceGate.Active;
                    recorder.Record(state, outputs);
                    if (i == 25)
                    {
                        Thread.Sleep(60);   // let the writer flush a first batch mid-recording
                    }
                }

                Assert.True(recorder.Stop(), "stopped in time");
                Assert.False(recorder.IsRecording, "stopped");
                Assert.True(recorder.LastError == null, "no error: " + recorder.LastError);
                Assert.Equal(50L, recorder.RecordedCount, "rows written");
                Assert.Equal(0L, recorder.DroppedCount, "nothing dropped");

                List<BalanceRecord> rows = BalanceCsv.Read(recorder.FilePath).ToList();
                Assert.Equal(50, rows.Count, "rows read");
                Assert.Near(69, rows[49].V, 0, "last V");
                Assert.Near(-49, rows[49].AlphaRL, 0, "alpha RL");
                Assert.Near(0.49, rows[49].Understeer, 0, "US");
                Assert.Equal(BalanceGate.Active, rows[49].Gate, "gate");

                // A second recording in the same second gets a distinct file.
                string first = recorder.FilePath;
                recorder.Start(directory, "LMU", "Ligier JS P320");
                recorder.Record(state, outputs);
                recorder.Stop();
                Assert.True(recorder.FilePath != first, "unique file name");
                Assert.Equal(1, BalanceCsv.Read(recorder.FilePath).Count(), "second file");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void Recorder_ReportsUnwritableDirectory()
    {
        string blocker = Path.Combine(Path.GetTempPath(), "SlipLockSourceTests_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(blocker, "a file where the directory should be");
        try
        {
            using (var recorder = new BalanceRecorder(NullLog.Instance, capacity: 16, flushIntervalMs: 10))
            {
                recorder.Start(blocker, "LMU", "Car");
                for (int i = 0; i < 100 && recorder.IsRecording; i++)
                {
                    Thread.Sleep(10);
                }

                Assert.False(recorder.IsRecording, "recording stops on IO error");
                Assert.True(recorder.Stop(), "stop after failure");
                Assert.True(!string.IsNullOrEmpty(recorder.LastError), "error reported");
            }
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Test]
    public void ReplayTool_MissingFileReturnsError()
    {
        var output = new StringWriter();
        Assert.Equal(ReplayTool.ExitFileError, ReplayTool.Run(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".csv"), output));
        Assert.True(output.ToString().Contains("not found"), "message");
    }

    [Test]
    public void ReplayTool_ReplaysRecording()
    {
        string path = Path.Combine(Path.GetTempPath(), "SlipLockReplay_" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            using (var writer = new StreamWriter(path))
            {
                BalanceCsv.WriteHeader(writer);
                var state = new VehicleState();
                for (int i = 0; i < 600; i++)
                {
                    // 10 s steady cornering at 30 m/s on a 100 m radius.
                    state.Valid = true;
                    state.SimTime = i / 60.0;
                    state.WallTime = state.SimTime;
                    state.V = 30;
                    state.Vy = 0.3;
                    state.R = 0.3;
                    state.Ay = 9.0;
                    state.Ax = 0;
                    state.Az = MathUtil.Gravity;
                    state.Theta = 0.35;
                    state.ThetaMax = 4.7;
                    state.Throttle = 0.4;
                    state.Gear = 4;
                    state.OnTrack = true;
                    state.Surface = SurfaceKind.Asphalt;
                    var record = default(BalanceRecord);
                    record.CopyFrom(state, null);
                    BalanceCsv.WriteRow(writer, ref record);
                }
            }

            var output = new StringWriter();
            int exitCode = ReplayTool.Run(path, output);
            Assert.Equal(ReplayTool.ExitOk, exitCode, output.ToString());
            string text = output.ToString();
            Assert.True(text.Contains("Samples:") && text.Contains("600"), "summary: " + text);
            Assert.True(text.Contains("Understeer:") && text.Contains("G:"), "summary sections");

            File.WriteAllText(path, BalanceCsv.Header + Environment.NewLine);
            Assert.Equal(ReplayTool.ExitNoSamples, ReplayTool.Run(path, new StringWriter()), "empty recording");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// AppDomain allocation monitoring has allocation-context granularity (~8 KB), so a truly allocation-free loop
    /// can still report up to one quantum. Any per-call allocation over 20 000 calls would be far above this.
    /// </summary>
    private const long AllocationSlackBytes = 16 * 1024;

    private static FrameContext Ctx(double wallTime) => new FrameContext { GameRunning = true, WallTime = wallTime };

    private static FakeTelemetryReader IRacingReader() => new FakeTelemetryReader()
        .Set(IR + "SessionTime", 123.5)
        .Set(IR + "VelocityX", 40.0f)
        .Set(IR + "VelocityY", -1.5f)
        .Set(IR + "Speed", 40.03f)
        .Set(IR + "SteeringWheelAngle", 0.2f)
        .Set(IR + "SteeringWheelAngleMax", 4.0f)
        .Set(IR + "YawRate", 0.3f)
        .Set(IR + "LatAccel", 12.0f)
        .Set(IR + "LongAccel", -3.0f)
        .Set(IR + "VertAccel", 9.8f)
        .Set(IR + "OnPitRoad", false)
        .Set(IR + "IsInGarage", false)
        .Set(IR + "IsOnTrack", true)
        .Set(IR + "IsReplayPlaying", false);

    private static FakeTelemetryReader RFactorReader()
    {
        FakeTelemetryReader reader = new FakeTelemetryReader()
            .Set(RT + "mElapsedTime", 812.25)
            .Set(RT + "mLocalVel.x", 0.8)
            .Set(RT + "mLocalVel.z", -45.0)
            .Set(RT + "mLocalRot.y", 0.25)
            .Set(RT + "mLocalAccel.x", 11.0)
            .Set(RT + "mLocalAccel.y", 9.81)
            .Set(RT + "mLocalAccel.z", 4.0)
            .Set(RT + "mFilteredSteering", -0.1)
            .Set(RT + "mPhysicalSteeringWheelRange", 400.0f)
            .Set(RT + "mVisualSteeringWheelRange", 400.0f)
            .Set(RT + "mFilteredThrottle", 0.5)
            .Set(RT + "mFilteredBrake", 0.0)
            .Set(RT + "mGear", 3)
            .Set(RT + "mLastImpactET", 0.0)
            .Set(RS + "mInGarageStall", (byte)0)
            .Set(RS + "mInPits", (byte)0)
            .Set(RS + "mControl", (sbyte)0);
        for (int i = 0; i < Wheels.Count; i++)
        {
            reader.Set(RT + "mWheels" + Wheels.RawSuffixes[i] + ".mSurfaceType", (byte)0);
            SetWheel(reader, i, lateralPatch: 0.5, longitudinalGround: -45.0);
        }

        return reader;
    }

    private static void SetWheel(FakeTelemetryReader reader, int wheel, double lateralPatch, double longitudinalGround)
    {
        string prefix = RT + "mWheels" + Wheels.RawSuffixes[wheel];
        reader.Set(prefix + ".mLateralPatchVel", lateralPatch).Set(prefix + ".mLongitudinalGroundVel", longitudinalGround);
    }

    private static SurfaceKind Surface(byte fl, byte fr, byte rl, byte rr)
    {
        FakeTelemetryReader reader = RFactorReader();
        byte[] types = { fl, fr, rl, rr };
        for (int i = 0; i < Wheels.Count; i++)
        {
            reader.Set(RT + "mWheels" + Wheels.RawSuffixes[i] + ".mSurfaceType", types[i]);
        }

        var state = new VehicleState();
        new RFactorStateSource().Read(Ctx(0), reader, state);
        return state.Surface;
    }

    private static FakeTelemetryReader AccReader(bool camel, bool slipAngles)
    {
        string velocity = camel ? "localVelocity" : "LocalVelocity";
        string angular = camel ? "localAngularVel" : "LocalAngularVelocity";
        string accG = camel ? "accG" : "AccG";
        FakeTelemetryReader reader = new FakeTelemetryReader()
            .Set(AP + (camel ? "steerAngle" : "SteerAngle"), 0.25f)
            .Set(AP + velocity + "01", 1.25f)
            .Set(AP + velocity + "02", 0.0f)
            .Set(AP + velocity + "03", 38.0f)
            .Set(AP + angular + "01", 0.0f)
            .Set(AP + angular + "02", 0.5f)
            .Set(AP + angular + "03", 0.0f)
            .Set(AP + accG + "01", 1.5f)
            .Set(AP + accG + "02", 1.0f)
            .Set(AP + accG + "03", -0.5f);
        if (slipAngles)
        {
            reader.Set(AP + "slipAngle01", 0.04f).Set(AP + "slipAngle02", -0.06f).Set(AP + "slipAngle03", 0.02f).Set(AP + "slipAngle04", -0.03f);
        }

        return reader;
    }

    private static void AssertAccValues(VehicleState state)
    {
        Assert.Near(38.0, state.V, 1e-6, "V = LocalVelocity z");
        Assert.Near(1.25, state.Vy, 1e-6, "Vy = LocalVelocity x");
        Assert.Near(0.5, state.R, 1e-6, "R = angular y");
        Assert.Near(1.5 * MathUtil.Gravity, state.Ay, 1e-5, "Ay g → m/s²");
        Assert.Near(-0.5 * MathUtil.Gravity, state.Ax, 1e-5, "Ax = AccG z");
        Assert.Near(1.0 * MathUtil.Gravity, state.Az, 1e-5, "Az = AccG y");
        Assert.Near(270.0 * MathUtil.DegToRad, state.ThetaMax, 1e-9, "assumed half lock");
        Assert.Near(0.25 * 270.0 * MathUtil.DegToRad, state.Theta, 1e-6, "theta");
    }

    private static void AssertNoAllocations(string name, IVehicleStateSource source, FakeTelemetryReader reader)
    {
        var state = new VehicleState();
        var ctx = Ctx(0);

        // Warm-up (path resolution, JIT) with probing wall times.
        for (int i = 0; i < 200; i++)
        {
            ctx.WallTime = i * 0.1;
            source.Read(ctx, reader, state);
        }

        long allocated = MeasureAllocations(() =>
        {
            for (int i = 0; i < 20000; i++)
            {
                ctx.WallTime = 20.0 + (i * 0.01);   // includes ~200 probe frames
                source.Read(ctx, reader, state);
            }
        });
        Assert.True(allocated < AllocationSlackBytes, name + " allocated " + allocated + " bytes in 20000 reads");
    }

    private static long MeasureAllocations(Action action)
    {
        AppDomain.MonitoringIsEnabled = true;
        action(); // JIT the delegate body outside the measurement
        GC.Collect();
        long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        action();
        return AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
    }

    private static VehicleState RandomState(Random random, int index)
    {
        var s = new VehicleState
        {
            Valid = random.Next(2) == 0,
            SimTime = index * 0.016667,
            WallTime = 1000 + (index * 0.0166),
            V = RandomDouble(random),
            Vy = RandomDouble(random),
            Theta = RandomDouble(random),
            ThetaMax = index % 7 == 0 ? double.NaN : 4.71238898038469,
            R = RandomDouble(random),
            Ay = RandomDouble(random),
            Ax = index % 11 == 0 ? double.PositiveInfinity : RandomDouble(random),
            Az = index % 13 == 0 ? double.NegativeInfinity : RandomDouble(random),
            Throttle = random.NextDouble(),
            Brake = random.NextDouble(),
            Gear = random.Next(-1, 8),
            Surface = (SurfaceKind)random.Next(0, 5),
            OnPitRoad = random.Next(2) == 0,
            OnTrack = random.Next(2) == 0,
            InGarage = random.Next(2) == 0,
            IsReplay = random.Next(2) == 0,
            IsSpectating = random.Next(2) == 0,
            IsPaused = random.Next(2) == 0,
            Wetness = index % 3 == 0 ? double.NaN : random.NextDouble(),
            ContactCounter = random.Next(0, 1000),
            HasSlipAngles = random.Next(2) == 0,
            AlphaFront = RandomDouble(random),
            AlphaRear = double.Epsilon * index,
            SlipAnglesInRadians = random.Next(2) == 0,
        };
        for (int i = 0; i < Wheels.Count; i++)
        {
            s.Alpha[i] = RandomDouble(random);
        }

        return s;
    }

    private static BalanceOutputs RandomOutputs(Random random) => new BalanceOutputs
    {
        Understeer = random.NextDouble(),
        Oversteer = random.NextDouble(),
        YawRatio = random.Next(4) == 0 ? double.NaN : RandomDouble(random),
        YawRef = RandomDouble(random),
        BodySlipDeg = RandomDouble(random),
        Gate = (BalanceGate)random.Next(0, 14),
    };

    /// <summary>Wide-range values incl. NaN, negatives, tiny and huge magnitudes (exercises round-trip formatting).</summary>
    private static double RandomDouble(Random random)
    {
        switch (random.Next(8))
        {
            case 0:
                return double.NaN;
            case 1:
                return -random.NextDouble() * 1e-12;
            case 2:
                return random.NextDouble() * 1e12;
            case 3:
                return (float)(random.NextDouble() * 100);
            default:
                return (random.NextDouble() - 0.5) * 200;
        }
    }

    private static void AssertStatesEqual(VehicleState e, VehicleState a, string row)
    {
        Assert.Equal(e.Valid, a.Valid, row + " Valid");
        AssertSame(e.SimTime, a.SimTime, row + " SimTime");
        AssertSame(e.WallTime, a.WallTime, row + " WallTime");
        AssertSame(e.V, a.V, row + " V");
        AssertSame(e.Vy, a.Vy, row + " Vy");
        AssertSame(e.Theta, a.Theta, row + " Theta");
        AssertSame(e.ThetaMax, a.ThetaMax, row + " ThetaMax");
        AssertSame(e.R, a.R, row + " R");
        AssertSame(e.Ay, a.Ay, row + " Ay");
        AssertSame(e.Ax, a.Ax, row + " Ax");
        AssertSame(e.Az, a.Az, row + " Az");
        AssertSame(e.Throttle, a.Throttle, row + " Throttle");
        AssertSame(e.Brake, a.Brake, row + " Brake");
        Assert.Equal(e.Gear, a.Gear, row + " Gear");
        Assert.Equal(e.Surface, a.Surface, row + " Surface");
        Assert.Equal(e.OnPitRoad, a.OnPitRoad, row + " OnPitRoad");
        Assert.Equal(e.OnTrack, a.OnTrack, row + " OnTrack");
        Assert.Equal(e.InGarage, a.InGarage, row + " InGarage");
        Assert.Equal(e.IsReplay, a.IsReplay, row + " IsReplay");
        Assert.Equal(e.IsSpectating, a.IsSpectating, row + " IsSpectating");
        Assert.Equal(e.IsPaused, a.IsPaused, row + " IsPaused");
        AssertSame(e.Wetness, a.Wetness, row + " Wetness");
        Assert.Equal(e.ContactCounter, a.ContactCounter, row + " ContactCounter");
        Assert.Equal(e.HasSlipAngles, a.HasSlipAngles, row + " HasSlipAngles");
        AssertSame(e.AlphaFront, a.AlphaFront, row + " AlphaFront");
        AssertSame(e.AlphaRear, a.AlphaRear, row + " AlphaRear");
        Assert.Equal(e.SlipAnglesInRadians, a.SlipAnglesInRadians, row + " SlipAnglesInRadians");
        for (int i = 0; i < Wheels.Count; i++)
        {
            AssertSame(e.Alpha[i], a.Alpha[i], row + " Alpha" + i);
        }
    }

    /// <summary>Bit-exact equality where NaN equals NaN (double.Equals semantics).</summary>
    private static void AssertSame(double expected, double actual, string message) =>
        Assert.True(expected.Equals(actual), message + ": expected " + expected.ToString("R") + " but was " + actual.ToString("R"));
}
