using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using User.SlipLockPropertiesCalc.Balance;
using User.SlipLockPropertiesCalc.Core;
using User.SlipLockPropertiesCalc.Settings;
using User.SlipLockPropertiesCalc.SlipLock;
using User.SlipLockPropertiesCalc.Telemetry;

namespace User.SlipLockPropertiesCalc.Tests;

/// <summary>
/// Shared contract types: math helpers, envelope, gear parsing, telemetry reader helpers, game presets, max-G
/// tracking, plugin settings (including a real v1 settings file) and car profiles.
/// </summary>
internal static class CoreTests
{
    /// <summary>A v1 <c>SlipLockPropertiesCalc.GeneralSettings.json</c> taken from the user's machine.</summary>
    private const string V1SettingsJson =
        "{\"SpeedWarningLevel\":100,\"SlipThrottleBlend\":20.0,\"TCThrottleBlend\":50.023490258166667,\"LockBrakeBlend\":20.0," +
        "\"ABSBrakeBlend\":50.404698051633375,\"SlipThreshold\":5.0967317914924566,\"LockThreshold\":5.0,\"TCThreshold\":5.0," +
        "\"ABSThreshold\":5.0,\"GateSlipOnThrottle\":true,\"GateLockOnBrake\":true,\"SlipAttackMs\":10.0,\"SlipReleaseMs\":100.0," +
        "\"LockAttackMs\":10.0,\"LockReleaseMs\":100.0,\"ABSAttackMs\":5.0,\"ABSReleaseMs\":51.84066411426911,\"TCAttackMs\":5.0," +
        "\"TCReleaseMs\":50.000000000000064,\"GameCapabilities\":{\"IRacing\":{\"WheelSpeedMode\":\"Mono\",\"ABSMode\":\"Available\"," +
        "\"TCMode\":\"Unknown\"},\"LMU\":{\"WheelSpeedMode\":\"Mono\",\"ABSMode\":\"Available\",\"TCMode\":\"Available\"}}}";

    // ------------------------------------------------------------------------------------------------------------
    // MathUtil
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void Map_LinearBetweenOnsetAndFull()
    {
        Assert.Equal(0.0, MathUtil.Map(-1, 0, 1), "below onset");
        Assert.Equal(0.0, MathUtil.Map(0, 0, 1), "at onset");
        Assert.Near(0.5, MathUtil.Map(0.5, 0, 1), 1e-15, "midpoint");
        Assert.Near(0.25, MathUtil.Map(3, 2, 6), 1e-15, "offset range");
        Assert.Equal(1.0, MathUtil.Map(1, 0, 1), "at full");
        Assert.Equal(1.0, MathUtil.Map(5, 0, 1), "above full");
    }

    [Test]
    public static void Map_AppliesGammaInsideTheRangeOnly()
    {
        Assert.Near(0.25, MathUtil.Map(0.5, 0, 1, 2.0), 1e-15, "gamma 2");
        Assert.Near(Math.Sqrt(0.5), MathUtil.Map(0.5, 0, 1, 0.5), 1e-15, "gamma 0.5");
        Assert.Equal(0.0, MathUtil.Map(0, 0, 1, 2.0), "gamma keeps 0");
        Assert.Equal(1.0, MathUtil.Map(2, 0, 1, 2.0), "gamma keeps 1");
    }

    [Test]
    public static void Map_ActsAsStepWhenFullIsNotAboveOnset()
    {
        Assert.Equal(1.0, MathUtil.Map(0.6, 0.5, 0.5), "above equal bounds");
        Assert.Equal(0.0, MathUtil.Map(0.5, 0.5, 0.5), "at equal bounds");
        Assert.Equal(0.0, MathUtil.Map(0.4, 0.5, 0.2), "below inverted bounds");
        Assert.Equal(1.0, MathUtil.Map(0.7, 0.5, 0.2), "above inverted bounds");
    }

    [Test]
    public static void Map_NonFiniteInputMapsToZero()
    {
        Assert.Equal(0.0, MathUtil.Map(double.NaN, 0, 1), "NaN");
        Assert.Equal(0.0, MathUtil.Map(double.PositiveInfinity, 0, 1), "+inf");
        Assert.Equal(0.0, MathUtil.Map(double.NegativeInfinity, 0, 1), "-inf");
    }

    [Test]
    public static void Clamp_AndFiniteHelpers()
    {
        Assert.Equal(1.0, MathUtil.Clamp(0.5, 1, 2), "below");
        Assert.Equal(2.0, MathUtil.Clamp(3, 1, 2), "above");
        Assert.Equal(1.5, MathUtil.Clamp(1.5, 1, 2), "inside");
        Assert.Equal(0.0, MathUtil.Clamp01(-3), "clamp01 below");
        Assert.Equal(1.0, MathUtil.Clamp01(3), "clamp01 above");

        Assert.True(MathUtil.IsFinite(1.0), "finite");
        Assert.False(MathUtil.IsFinite(double.NaN), "NaN");
        Assert.False(MathUtil.IsFinite(double.NegativeInfinity), "-inf");
        Assert.Equal(7.0, MathUtil.FiniteOr(double.NaN, 7), "fallback");
        Assert.Equal(3.0, MathUtil.FiniteOr(3, 7), "value");

        Assert.Equal(1, MathUtil.Sign(0.1), "positive");
        Assert.Equal(-1, MathUtil.Sign(-0.1), "negative");
        Assert.Equal(0, MathUtil.Sign(0), "zero");
        Assert.Equal(0, MathUtil.Sign(double.NaN), "NaN");
    }

    [Test]
    public static void Average4_SumsLeftToRight()
    {
        // Chosen so the summation order changes the floating-point result.
        var values = new[] { 1e16, 1.0, -1e16, 1.0 };
        double leftToRight = (((values[0] + values[1]) + values[2]) + values[3]) / 4;
        double pairwise = ((values[0] + values[2]) + (values[1] + values[3])) / 4;
        Assert.True(leftToRight != pairwise, "precondition: order matters for these values");
        Assert.Equal(leftToRight, MathUtil.Average4(values), "left to right like v1");
        Assert.Equal(2.5, MathUtil.Average4(new[] { 1.0, 2.0, 3.0, 4.0 }), "plain mean");
    }

    [Test]
    public static void LagAlpha_FirstOrderFactor()
    {
        Assert.Equal(1.0, MathUtil.LagAlpha(0.01, 0), "zero tau passes through");
        Assert.Equal(1.0, MathUtil.LagAlpha(0.01, -1), "negative tau passes through");
        Assert.Near(0.1, MathUtil.LagAlpha(0.01, 0.09), 1e-15, "dt / (tau + dt)");
        Assert.Near(0.5, MathUtil.LagAlpha(0.2, 0.2), 1e-15, "dt == tau");
    }

    // ------------------------------------------------------------------------------------------------------------
    // LegacyEnvelope
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void Envelope_AttackIsExponentialOrInstant()
    {
        double state = 0;
        Assert.Equal(80.0, LegacyEnvelope.Apply(ref state, 80, 0.016, 0.5, 100), "attack < 1 ms is instant");

        state = 0;
        double output = LegacyEnvelope.Apply(ref state, 100, 0.01, 10, 100);
        Assert.Near(100 * (1 - Math.Exp(-1)), output, 1e-12, "one time constant");
        Assert.Equal(state, output, "output is the state");
    }

    [Test]
    public static void Envelope_ReleaseLingersAtHighLevelsAndSnapsToZero()
    {
        // Same absolute release step from a high and a low level: the high level keeps a larger share.
        double high = 90;
        LegacyEnvelope.Apply(ref high, 0, 0.016, 5, 200);
        double low = 10;
        LegacyEnvelope.Apply(ref low, 0, 0.016, 5, 200);
        Assert.True(high / 90 > low / 10, "concave release");

        double tiny = 0.0005;
        Assert.Equal(0.0, LegacyEnvelope.Apply(ref tiny, 0.0004, 0.016, 5, 200), "float dust snapped");
        Assert.Equal(0.0, tiny, "state snapped");

        double instant = 50;
        Assert.Equal(0.0, LegacyEnvelope.Apply(ref instant, 0, 0.016, 5, 0), "release < 1 ms is instant");
    }

    [Test]
    public static void Envelope_ClampsOutputButKeepsState()
    {
        double state = 0;
        Assert.Equal(100.0, LegacyEnvelope.Apply(ref state, 150, 0.016, 0, 100), "clamped output");
        Assert.Equal(150.0, state, "state keeps the target (v1)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // FrameContext / telemetry helpers
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void ParseGear_HandlesSimHubGearStrings()
    {
        Assert.Equal(0, FrameContext.ParseGear(null), "null");
        Assert.Equal(0, FrameContext.ParseGear(string.Empty), "empty");
        Assert.Equal(-1, FrameContext.ParseGear("R"), "R");
        Assert.Equal(-1, FrameContext.ParseGear("r"), "r");
        Assert.Equal(-1, FrameContext.ParseGear("-1"), "-1");
        Assert.Equal(0, FrameContext.ParseGear("N"), "N");
        Assert.Equal(1, FrameContext.ParseGear("1"), "1");
        Assert.Equal(12, FrameContext.ParseGear("12"), "12");
        Assert.Equal(3, FrameContext.ParseGear("3a"), "trailing text");
    }

    [Test]
    public static void TryConvertToDouble_AllBoxedTypes()
    {
        var cases = new List<KeyValuePair<object, double>>
        {
            new KeyValuePair<object, double>(1.5d, 1.5),
            new KeyValuePair<object, double>(2.5f, 2.5),
            new KeyValuePair<object, double>(-3, -3),
            new KeyValuePair<object, double>(4L, 4),
            new KeyValuePair<object, double>((short)-5, -5),
            new KeyValuePair<object, double>((byte)6, 6),
            new KeyValuePair<object, double>((sbyte)-7, -7),
            new KeyValuePair<object, double>(8u, 8),
            new KeyValuePair<object, double>((ushort)9, 9),
            new KeyValuePair<object, double>(10UL, 10), // IConvertible path
            new KeyValuePair<object, double>(1.25m, 1.25),
            new KeyValuePair<object, double>(true, 1),
            new KeyValuePair<object, double>(false, 0),
            new KeyValuePair<object, double>("1.5", 1.5), // invariant culture, independent of the OS locale
        };

        foreach (KeyValuePair<object, double> entry in cases)
        {
            string label = entry.Key.GetType().Name;
            Assert.True(TelemetryReaderExtensions.TryConvertToDouble(entry.Key, out double value), label + " converts");
            Assert.Equal(entry.Value, value, label);
        }

        foreach (object invalid in new object[] { null, "abc", 'x', new object(), DateTime.MinValue })
        {
            Assert.False(TelemetryReaderExtensions.TryConvertToDouble(invalid, out double value), "rejects " + (invalid?.GetType().Name ?? "null"));
            Assert.True(double.IsNaN(value), "NaN on failure");
        }
    }

    [Test]
    public static void ReaderHelpers_TypedReads()
    {
        var reader = new FakeTelemetryReader()
            .Set("a", 2.0)
            .Set("flag", true)
            .Set("zero", 0)
            .Set("text", "x");

        Assert.True(reader.Exists("a"), "exists");
        Assert.False(reader.Exists("missing"), "missing");
        Assert.False(reader.Exists(null), "null path");

        Assert.True(reader.TryGetDouble("a", out double a) && a == 2.0, "double");
        Assert.False(reader.TryGetDouble(null, out double nullPath), "null path");
        Assert.True(double.IsNaN(nullPath), "null path NaN");
        Assert.True(double.IsNaN(reader.GetDoubleOrNaN("missing")), "missing NaN");
        Assert.True(double.IsNaN(reader.GetDoubleOrNaN("text")), "non-numeric NaN");

        Assert.True(reader.TryGetBool("flag", out bool flag) && flag, "bool");
        Assert.True(reader.TryGetBool("zero", out bool zero) && !zero, "numeric zero is false");
        Assert.False(reader.TryGetBool("missing", out bool missing) || missing, "missing bool");
    }

    [Test]
    public static void GetText_StringsAndNulTerminatedBytes()
    {
        byte[] model = new byte[30];
        byte[] name = Encoding.ASCII.GetBytes("Ligier JS P325");
        Array.Copy(name, model, name.Length);
        model[name.Length + 2] = (byte)'X'; // garbage after the terminator must be ignored

        var reader = new FakeTelemetryReader()
            .Set("bytes", model)
            .Set("unterminated", Encoding.ASCII.GetBytes("GT3 "))
            .Set("empty", new byte[4])
            .Set("string", "  Hypercar ")
            .Set("number", 42);

        Assert.Equal("Ligier JS P325", reader.GetText("bytes"), "NUL-terminated");
        Assert.Equal("GT3", reader.GetText("unterminated"), "no terminator, trimmed");
        Assert.Equal(string.Empty, reader.GetText("empty"), "all NUL");
        Assert.Equal("Hypercar", reader.GetText("string"), "trimmed string");
        Assert.Equal("42", reader.GetText("number"), "number");
        Assert.Equal(null, reader.GetText("missing"), "missing");
        Assert.Equal(null, reader.GetText(null), "null path");
    }

    [Test]
    public static void ResolveFirst_ReturnsFirstExistingCandidate()
    {
        var reader = new FakeTelemetryReader().Set("second", 0.0).Set("third", 1.0);
        Assert.Equal("second", reader.ResolveFirst(new[] { "first", "second", "third" }), "first existing");
        Assert.Equal(null, reader.ResolveFirst(new[] { "x", "y" }), "none");
        Assert.Equal(null, reader.ResolveFirst(new string[0]), "empty");
    }

    // ------------------------------------------------------------------------------------------------------------
    // GamePresets / MaxGTracker
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void GamePresets_LookupIsCaseInsensitive()
    {
        GamePreset evo = GamePresets.Get("AssettoCorsaEVO", out string evoName);
        Assert.True(ReferenceEquals(GamePresets.Get("AssettoCorsaEvo", out _), evo), "EVO matches the Evo entry");
        Assert.Equal("AssettoCorsaEvo", evoName, "table key reported");
        Assert.Equal(10.0, evo.SlipLat, "AC values");

        GamePreset beam = GamePresets.Get("BeamNgDrive", out string beamName);
        Assert.Equal("BeamNGdrive", beamName, "BeamNG key");
        Assert.Equal(15.0, beam.SlipLat, "BeamNG values");
        Assert.False(ReferenceEquals(beam, GamePresets.Default), "not the default");

        GamePreset lmu = GamePresets.Get("lmu", out string lmuName);
        Assert.Equal("LMU", lmuName, "lower case");
        Assert.True(lmu.InverseSlipLoad && lmu.SpeedFadeKmh == 70 && lmu.PreCut == 15, "LMU values");
    }

    [Test]
    public static void GamePresets_UnknownFallsBackToDefault()
    {
        foreach (string game in new[] { "SomeNewSim", string.Empty, null })
        {
            Assert.True(ReferenceEquals(GamePresets.Default, GamePresets.Get(game, out string name)), "default preset");
            Assert.Equal(GamePresets.DefaultName, name, "default name");
        }
    }

    [Test]
    public static void MaxGTracker_GrowsRejectsSpikesAndResets()
    {
        var tracker = new MaxGTracker();
        Assert.Equal(MaxGTracker.Initial, tracker.MaxSway, "initial sway");
        Assert.Equal(MaxGTracker.Initial, tracker.MaxSurge, "initial surge");
        Assert.Equal(MaxGTracker.Initial, tracker.MaxDecel, "initial decel");

        tracker.Update(-7, 8);
        Assert.Equal(7.0, tracker.MaxSway, "|sway| grows the maximum");
        Assert.Equal(8.0, tracker.MaxSurge, "surge grows");

        tracker.Update(20, 0);
        Assert.Equal(7.0, tracker.MaxSway, "jump of 13 rejected");
        tracker.Update(12, 13);
        Assert.Equal(7.0, tracker.MaxSway, "jump of exactly 5 rejected");
        Assert.Equal(8.0, tracker.MaxSurge, "surge jump of 5 rejected");
        tracker.Update(11.9, 12.9);
        Assert.Equal(11.9, tracker.MaxSway, "jump below 5 accepted");
        Assert.Equal(12.9, tracker.MaxSurge, "surge jump below 5 accepted");

        tracker.Update(1, -9);
        Assert.Equal(9.0, tracker.MaxDecel, "negative surge grows decel");
        Assert.Equal(11.9, tracker.MaxSway, "smaller values do not shrink");

        tracker.Reset();
        Assert.Equal(MaxGTracker.Initial, tracker.MaxSway, "reset sway");
        Assert.Equal(MaxGTracker.Initial, tracker.MaxSurge, "reset surge");
        Assert.Equal(MaxGTracker.Initial, tracker.MaxDecel, "reset decel");
    }

    // ------------------------------------------------------------------------------------------------------------
    // PluginSettings
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void PluginSettings_LoadsV1File()
    {
        var settings = JsonConvert.DeserializeObject<PluginSettings>(V1SettingsJson);
        settings.Normalize();

        Assert.Equal(20.0, settings.SlipThrottleBlend, "SlipThrottleBlend");
        Assert.Equal(Parse("50.023490258166667"), settings.TCThrottleBlend, "TCThrottleBlend");
        Assert.Equal(20.0, settings.LockBrakeBlend, "LockBrakeBlend");
        Assert.Equal(Parse("50.404698051633375"), settings.ABSBrakeBlend, "ABSBrakeBlend");
        Assert.Equal(Parse("5.0967317914924566"), settings.SlipThreshold, "SlipThreshold");
        Assert.Equal(5.0, settings.LockThreshold, "LockThreshold");
        Assert.Equal(5.0, settings.TCThreshold, "TCThreshold");
        Assert.Equal(5.0, settings.ABSThreshold, "ABSThreshold");
        Assert.True(settings.GateSlipOnThrottle, "GateSlipOnThrottle");
        Assert.True(settings.GateLockOnBrake, "GateLockOnBrake");
        Assert.Equal(10.0, settings.SlipAttackMs, "SlipAttackMs");
        Assert.Equal(100.0, settings.SlipReleaseMs, "SlipReleaseMs");
        Assert.Equal(10.0, settings.LockAttackMs, "LockAttackMs");
        Assert.Equal(100.0, settings.LockReleaseMs, "LockReleaseMs");
        Assert.Equal(5.0, settings.ABSAttackMs, "ABSAttackMs");
        Assert.Equal(Parse("51.84066411426911"), settings.ABSReleaseMs, "ABSReleaseMs");
        Assert.Equal(5.0, settings.TCAttackMs, "TCAttackMs");
        Assert.Equal(Parse("50.000000000000064"), settings.TCReleaseMs, "TCReleaseMs");

        Assert.Equal(2, settings.GameCapabilities.Count, "two games");
        Assert.Equal(GameCapabilities.ModeMono, settings.GameCapabilities["iracing"].WheelSpeedMode, "iRacing mode (case-insensitive)");
        Assert.Equal(GameCapabilities.Available, settings.GameCapabilities["IRACING"].ABSMode, "iRacing ABS");
        Assert.Equal(GameCapabilities.ModeUnknown, settings.GameCapabilities["IRacing"].TCMode, "iRacing TC");
        Assert.Equal(GameCapabilities.Available, settings.GameCapabilities["lmu"].TCMode, "LMU TC");

        // v2 fields absent from the v1 file get their defaults.
        Assert.Equal(PluginSettings.CurrentSchemaVersion, settings.SchemaVersion, "schema version");
        Assert.Equal(2, settings.SchemaVersion, "schema version 2");
        Assert.False(settings.ShowDebugView, "debug view hidden by default");
        Assert.True(settings.UseShakeItWheelLock, "ShakeIT lock merge on by default");
        Assert.True(settings.Balance != null && settings.BalanceCalibration != null, "v2 objects present");
    }

    [Test]
    public static void PluginSettings_NormalizeRepairsNullsAndKeys()
    {
        var settings = new PluginSettings
        {
            GameCapabilities = new Dictionary<string, GameCapabilities>(StringComparer.Ordinal)
            {
                { "LMU", new GameCapabilities { WheelSpeedMode = GameCapabilities.ModeMono } },
                { "IRacing", null },
            },
            BalanceCalibration = null,
            Balance = null,
            SchemaVersion = 0,
        };

        settings.Normalize();

        Assert.Equal(GameCapabilities.ModeMono, settings.GameCapabilities["lmu"].WheelSpeedMode, "case-insensitive after normalize");
        Assert.True(settings.GameCapabilities["iracing"] != null, "null entry replaced");
        Assert.Equal(GameCapabilities.ModeUnknown, settings.GameCapabilities["IRacing"].WheelSpeedMode, "replacement is unknown");
        Assert.True(settings.BalanceCalibration != null && settings.BalanceCalibration.Count == 0, "calibration dictionary created");
        Assert.True(settings.Balance != null, "balance tuning created");
        Assert.Equal(PluginSettings.CurrentSchemaVersion, settings.SchemaVersion, "schema version");

        var nullCaps = new PluginSettings { GameCapabilities = null };
        nullCaps.Normalize();
        Assert.True(nullCaps.GameCapabilities != null && nullCaps.GameCapabilities.Count == 0, "null capabilities dictionary created");

        SimCalibration calibration = settings.GetCalibration("LMU");
        Assert.True(ReferenceEquals(calibration, settings.GetCalibration("lmu")), "calibration lookup case-insensitive");
    }

    [Test]
    public static void PluginSettings_NormalizeClampsValues()
    {
        var settings = new PluginSettings
        {
            SlipThrottleBlend = 150,
            TCThrottleBlend = -10,
            LockBrakeBlend = double.NaN,
            ABSBrakeBlend = double.PositiveInfinity,
            SlipThreshold = 70,
            LockThreshold = -1,
            SlipAttackMs = double.NaN,
            SlipReleaseMs = 5000,
            ABSReleaseMs = -5,
        };

        settings.Normalize();

        Assert.Equal(100.0, settings.SlipThrottleBlend, "blend clamped high");
        Assert.Equal(0.0, settings.TCThrottleBlend, "blend clamped low");
        Assert.Equal(20.0, settings.LockBrakeBlend, "NaN blend -> default");
        Assert.Equal(50.0, settings.ABSBrakeBlend, "infinite blend -> default");
        Assert.Equal(50.0, settings.SlipThreshold, "threshold clamped high");
        Assert.Equal(0.0, settings.LockThreshold, "threshold clamped low");
        Assert.Equal(10.0, settings.SlipAttackMs, "NaN attack -> default");
        Assert.Equal(2000.0, settings.SlipReleaseMs, "release clamped high");
        Assert.Equal(0.0, settings.ABSReleaseMs, "release clamped low");
    }

    [Test]
    public static void PluginSettings_CopyToAndResetDefaults()
    {
        var settings = JsonConvert.DeserializeObject<PluginSettings>(V1SettingsJson);
        settings.UseShakeItWheelLock = false;
        var tuning = new SlipLockTuning { SlipSensitivity = 2.0, LockSensitivity = 3.0 };
        settings.CopyTo(tuning);

        Assert.Equal(settings.TCThrottleBlend, tuning.TcThrottleBlend, "TC blend");
        Assert.Equal(settings.ABSBrakeBlend, tuning.AbsBrakeBlend, "ABS blend");
        Assert.Equal(settings.SlipThreshold, tuning.SlipThreshold, "slip threshold");
        Assert.Equal(settings.ABSReleaseMs, tuning.AbsReleaseMs, "ABS release");
        Assert.Equal(settings.TCReleaseMs, tuning.TcReleaseMs, "TC release");
        Assert.True(tuning.GateSlipOnThrottle && tuning.GateLockOnBrake, "gates");
        Assert.False(tuning.UseShakeItWheelLock, "ShakeIT toggle");
        Assert.Equal(2.0, tuning.SlipSensitivity, "sensitivities untouched");
        Assert.Equal(3.0, tuning.LockSensitivity, "sensitivities untouched");

        settings.ResetSlipLockTuningToDefaults();
        var defaults = new PluginSettings();
        Assert.Equal(defaults.TCThrottleBlend, settings.TCThrottleBlend, "blend reset");
        Assert.Equal(defaults.ABSReleaseMs, settings.ABSReleaseMs, "release reset");
        Assert.Equal(defaults.GateSlipOnThrottle, settings.GateSlipOnThrottle, "gate reset");
        Assert.True(settings.UseShakeItWheelLock, "toggle reset");
        Assert.Equal(2, settings.GameCapabilities.Count, "capabilities kept");
    }

    // ------------------------------------------------------------------------------------------------------------
    // CarProfile
    // ------------------------------------------------------------------------------------------------------------

    [Test]
    public static void CarProfile_SensitivityAccessorsClamp()
    {
        var profile = new CarProfile();
        foreach (SensitivityKind kind in new[] { SensitivityKind.Slip, SensitivityKind.Lock, SensitivityKind.Understeer, SensitivityKind.Oversteer })
        {
            Assert.Equal(CarProfile.DefaultSensitivity, profile.GetSensitivity(kind), kind + " default");
        }

        profile.SetSensitivity(SensitivityKind.Slip, 5);
        profile.SetSensitivity(SensitivityKind.Lock, 1000);
        profile.SetSensitivity(SensitivityKind.Understeer, double.NaN);
        profile.SetSensitivity(SensitivityKind.Oversteer, 250);

        Assert.Equal(CarProfile.MinSensitivity, profile.SlipSensitivity, "slip clamped low");
        Assert.Equal(CarProfile.MaxSensitivity, profile.LockSensitivity, "lock clamped high");
        Assert.Equal(CarProfile.DefaultSensitivity, profile.UndersteerSensitivity, "NaN -> default");
        Assert.Equal(250.0, profile.OversteerSensitivity, "in range");
        Assert.Equal(profile.LockSensitivity, profile.GetSensitivity(SensitivityKind.Lock), "getter maps kind");
        Assert.Equal(profile.OversteerSensitivity, profile.GetSensitivity(SensitivityKind.Oversteer), "getter maps kind");

        profile.SetSensitivity((SensitivityKind)99, 300);
        Assert.Equal(CarProfile.DefaultSensitivity, profile.GetSensitivity((SensitivityKind)99), "unknown kind");
        Assert.Equal(250.0, profile.OversteerSensitivity, "unknown kind changes nothing");
    }

    [Test]
    public static void CarProfile_NormalizeRepairsDeserializedInstance()
    {
        var profile = new CarProfile
        {
            SchemaVersion = 0,
            SimKey = null,
            CarKey = null,
            DisplayName = null,
            CarClass = null,
            SlipSensitivity = 0,
            LockSensitivity = 9999,
            UndersteerSensitivity = double.NaN,
            OversteerSensitivity = 120,
            Overrides = null,
            Learned = null,
        };

        profile.Normalize();

        Assert.Equal(string.Empty, profile.SimKey, "sim key");
        Assert.Equal(string.Empty, profile.CarKey, "car key");
        Assert.Equal(string.Empty, profile.DisplayName, "display name");
        Assert.Equal(string.Empty, profile.CarClass, "car class");
        Assert.Equal(CarProfile.MinSensitivity, profile.SlipSensitivity, "slip");
        Assert.Equal(CarProfile.MaxSensitivity, profile.LockSensitivity, "lock");
        Assert.Equal(CarProfile.DefaultSensitivity, profile.UndersteerSensitivity, "understeer");
        Assert.Equal(120.0, profile.OversteerSensitivity, "oversteer");
        Assert.True(profile.Overrides != null && profile.Learned != null, "objects created");
        Assert.Equal(CarProfile.CurrentSchemaVersion, profile.SchemaVersion, "schema version");
    }

    [Test]
    public static void CarProfile_JsonRoundTripKeepsSensitivities()
    {
        var profile = new CarProfile { SimKey = "LMU", CarKey = "Ligier JS P325", CarClass = "LMP3" };
        profile.SetSensitivity(SensitivityKind.Lock, 320);
        string json = JsonConvert.SerializeObject(profile);
        var loaded = JsonConvert.DeserializeObject<CarProfile>(json);
        loaded.Normalize();

        Assert.Equal("Ligier JS P325", loaded.CarKey, "car key");
        Assert.Equal(320.0, loaded.LockSensitivity, "lock sensitivity");
        Assert.Equal(CarProfile.DefaultSensitivity, loaded.SlipSensitivity, "slip default");
    }

    private static double Parse(string literal) => double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);
}
